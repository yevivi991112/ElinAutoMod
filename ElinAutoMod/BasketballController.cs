using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ElinAutoMod;

/// <summary>
/// 篮球专用鼠标连点功能。完全独立于 AutoController 的自动脚本体系：
/// 不读写 AutoController 的任何字段，有自己的开关状态、热键、配置文件。
///
/// 行为：按住鼠标左键 HoldSeconds 秒 -> 松开 -> 等待 IntervalMs 毫秒 -> 循环，
/// 直到玩家精力(stamina) &lt;= StopStaminaThreshold 时自动停止。
///
/// 鼠标事件通过 Win32 mouse_event 发出，不携带位移量，所以点击落在系统当前
/// 光标真实所在的位置（不会自动挪动鼠标去找目标）——这是系统级点击，会影响
/// 当前鼠标下方的任意窗口，不局限于游戏窗口。
/// </summary>
public class BasketballController : MonoBehaviour
{
	// ---------- Win32：模拟真实鼠标左键按下/抬起 ----------
	[DllImport("user32.dll")]
	private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

	private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
	private const uint MOUSEEVENTF_LEFTUP = 0x0004;

	public enum RunState
	{
		Idle,
		Holding,
		Waiting
	}

	// ---------- 可配置参数（会持久化到独立配置文件，不进 Auto 的预设系统） ----------
	public bool Enabled;

	public float HoldSeconds = 0.65f;

	public float IntervalMs = 300f;

	public int StopStaminaThreshold = 1;

	public const float MinHoldSeconds = 0.1f;

	public const float MaxHoldSeconds = 3f;

	public const float MinIntervalMs = 0f;

	public const float MaxIntervalMs = 3000f;

	public RunState CurrentState { get; private set; } = RunState.Idle;

	private Coroutine _loopCoroutine;

	// ---------- 拉杆机：固定按下序列，不判定精力，触发后只跑一轮，跟上面的循环功能各自独立 ----------
	// 按下-间隔1-按下-间隔2-按下-间隔3-按下，共4次按下、3段间隔。间隔可在UI里自定义，会持久化。
	public int LeverInterval1Ms = 890;

	public int LeverInterval2Ms = 1665;

	public int LeverInterval3Ms = 1380;

	public const int MinLeverIntervalMs = 0;

	public const int MaxLeverIntervalMs = 10000;

	// 每次"按下"内部按下到抬起之间的极短停留，纯粹是为了让系统/前台窗口把它识别成一次正常点击，
	// 不是可调节参数，不暴露给UI。
	private const float ClickPulseSeconds = 0.03f;

	public bool LeverRunning { get; private set; }

	private Coroutine _leverCoroutine;

	private bool _isMouseDown;

	// ---------- 独立配置文件：跟 mod DLL 走，不跟随 AutoController 的预设切换 ----------
	private static readonly string _modRootPath = GetModRootPath();

	private static string _configPath => Path.Combine(_modRootPath, "basketball_config.txt");

	private static string GetModRootPath()
	{
		try
		{
			string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			if (!string.IsNullOrEmpty(dllDir))
			{
				return Path.Combine(dllDir, "ElinAutoModConfig");
			}
		}
		catch
		{
		}
		return "D:\\CSZccapp\\ElinAutoMod";
	}

	private void Start()
	{
		LoadConfig();
	}

	private void Update()
	{
		// 默认热键 Alt+B，跟 AutoController 的 Alt+Q 和 ModUI 的 F12 都不冲突
		if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.B))
		{
			ToggleEnabled();
		}
		// 拉杆机热键 Alt+A，触发后只跑一轮固定序列
		if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.A))
		{
			TriggerLever();
		}
	}

	/// <summary>热键和UI按钮共用的启停入口。</summary>
	public void ToggleEnabled()
	{
		if (Enabled)
		{
			StopLoop();
		}
		else
		{
			StartLoop();
		}
	}

	public void StartLoop()
	{
		if (Enabled)
		{
			return;
		}
		if (LeverRunning)
		{
			return;
		}
		if (EClass.pc == null)
		{
			return;
		}
		Enabled = true;
		_loopCoroutine = StartCoroutine(RunLoop());
	}

	public void StopLoop()
	{
		Enabled = false;
		if (_loopCoroutine != null)
		{
			StopCoroutine(_loopCoroutine);
			_loopCoroutine = null;
		}
		// 关键：StopCoroutine 会在协程当前 yield 点直接掐断，如果这时鼠标正按着，
		// 后面负责松开的那行代码根本没机会执行——必须在这里强制补一次松开，
		// 否则真的会把物理左键"卡死"在按下状态。
		ForceReleaseIfNeeded();
		CurrentState = RunState.Idle;
	}

	private IEnumerator RunLoop()
	{
		while (Enabled)
		{
			if (EClass.pc == null || EClass.pc.isDead || EClass.pc.stamina.value <= StopStaminaThreshold)
			{
				break;
			}

			CurrentState = RunState.Holding;
			mouse_event(MOUSEEVENTF_LEFTDOWN, 0u, 0u, 0u, UIntPtr.Zero);
			_isMouseDown = true;
			yield return new WaitForSecondsRealtime(Mathf.Clamp(HoldSeconds, MinHoldSeconds, MaxHoldSeconds));

			mouse_event(MOUSEEVENTF_LEFTUP, 0u, 0u, 0u, UIntPtr.Zero);
			_isMouseDown = false;

			if (EClass.pc == null || EClass.pc.isDead || EClass.pc.stamina.value <= StopStaminaThreshold)
			{
				break;
			}

			CurrentState = RunState.Waiting;
			float waitSec = Mathf.Clamp(IntervalMs, MinIntervalMs, MaxIntervalMs) / 1000f;
			if (waitSec > 0f)
			{
				yield return new WaitForSecondsRealtime(waitSec);
			}
		}

		ForceReleaseIfNeeded();
		Enabled = false;
		CurrentState = RunState.Idle;
		_loopCoroutine = null;
	}

	private void ForceReleaseIfNeeded()
	{
		if (_isMouseDown)
		{
			mouse_event(MOUSEEVENTF_LEFTUP, 0u, 0u, 0u, UIntPtr.Zero);
			_isMouseDown = false;
		}
	}

	/// <summary>拉杆机热键/按钮共用的触发入口：按下-890ms-按下-1665ms-按下-1380ms-按下，共4次，只跑一轮。
	/// 不判定精力；跟上面的循环功能(Enabled)互斥，避免两边同时操作鼠标状态打架。</summary>
	public void TriggerLever()
	{
		if (LeverRunning)
		{
			return;
		}
		if (Enabled)
		{
			return;
		}
		if (EClass.pc == null)
		{
			return;
		}
		_leverCoroutine = StartCoroutine(LeverSequenceCoroutine());
	}

	private IEnumerator LeverSequenceCoroutine()
	{
		LeverRunning = true;
		int[] intervals = new int[3] { LeverInterval1Ms, LeverInterval2Ms, LeverInterval3Ms };
		for (int i = 0; i < 4; i++)
		{
			yield return DoClick();
			if (i < intervals.Length)
			{
				float waitSec = Mathf.Clamp(intervals[i], MinLeverIntervalMs, MaxLeverIntervalMs) / 1000f;
				yield return new WaitForSecondsRealtime(waitSec);
			}
		}
		LeverRunning = false;
		_leverCoroutine = null;
	}

	/// <summary>单次"按下"：按下 -> 极短停留 -> 抬起，模拟一次正常点击。</summary>
	private IEnumerator DoClick()
	{
		mouse_event(MOUSEEVENTF_LEFTDOWN, 0u, 0u, 0u, UIntPtr.Zero);
		_isMouseDown = true;
		yield return new WaitForSecondsRealtime(ClickPulseSeconds);
		mouse_event(MOUSEEVENTF_LEFTUP, 0u, 0u, 0u, UIntPtr.Zero);
		_isMouseDown = false;
	}

	public void SaveConfig()
	{
		try
		{
			Directory.CreateDirectory(_modRootPath);
			using (StreamWriter streamWriter = new StreamWriter(_configPath, false))
			{
				streamWriter.WriteLine("HoldSeconds=" + HoldSeconds.ToString(CultureInfo.InvariantCulture));
				streamWriter.WriteLine("IntervalMs=" + IntervalMs.ToString(CultureInfo.InvariantCulture));
				streamWriter.WriteLine("StopStaminaThreshold=" + StopStaminaThreshold);
				streamWriter.WriteLine("LeverInterval1Ms=" + LeverInterval1Ms);
				streamWriter.WriteLine("LeverInterval2Ms=" + LeverInterval2Ms);
				streamWriter.WriteLine("LeverInterval3Ms=" + LeverInterval3Ms);
			}
		}
		catch
		{
		}
	}

	public void LoadConfig()
	{
		try
		{
			if (!File.Exists(_configPath))
			{
				return;
			}
			string[] array = File.ReadAllLines(_configPath);
			foreach (string text in array)
			{
				if (string.IsNullOrEmpty(text))
				{
					continue;
				}
				string[] array2 = text.Split('=');
				if (array2.Length != 2)
				{
					continue;
				}
				string text2 = array2[0].Trim();
				string text3 = array2[1].Trim();
				try
				{
					switch (text2)
					{
					case "HoldSeconds":
						HoldSeconds = float.Parse(text3, CultureInfo.InvariantCulture);
						break;
					case "IntervalMs":
						IntervalMs = float.Parse(text3, CultureInfo.InvariantCulture);
						break;
					case "StopStaminaThreshold":
						StopStaminaThreshold = int.Parse(text3);
						break;
					case "LeverInterval1Ms":
						LeverInterval1Ms = int.Parse(text3);
						break;
					case "LeverInterval2Ms":
						LeverInterval2Ms = int.Parse(text3);
						break;
					case "LeverInterval3Ms":
						LeverInterval3Ms = int.Parse(text3);
						break;
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	private void OnDestroy()
	{
		ForceReleaseIfNeeded();
	}

	private void OnApplicationQuit()
	{
		ForceReleaseIfNeeded();
	}
}
