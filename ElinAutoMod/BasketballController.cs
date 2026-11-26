using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ElinAutoMod;

/// <summary>
/// 赌场小游戏助手（独立于 AutoController）：
/// 1. 投篮（原生接口版）：通过运行时反射直接调用 Minigame.Basket.dll 的 Shooter.Instance.ShootBall(HoldSeconds)，
///    彻底告别 Win32 mouse_event 模拟鼠标，不抢光标、不卡左键、可自由移走鼠标，稳定刷 108 号投掷技能经验。
/// 2. 老虎机 + Bonus 奖励关全自动必中（方案2）：通过运行时反射扫描场景中所有激活的 CSFramework.CustomSlot
///    （同时覆盖主老虎机与 ElosBonusGame.bonusSlot 奖励关），开转自动锁定最高赔率符号。
/// </summary>
public class BasketballController : MonoBehaviour
{
	public enum RunState
	{
		Idle,
		Holding,
		Waiting
	}

	public enum SlotRewardMode
	{
		MaxCoin,
		FreeSpin,
		Bonus
	}

	// ---------- 投篮参数（持久化到独立配置文件） ----------
	public bool Enabled;

	public float HoldSeconds = 0.65f;

	public float IntervalMs = 300f;

	public int StopStaminaThreshold = 1;

	public const float MinHoldSeconds = 0.1f;

	public const float MaxHoldSeconds = 3f;

	public const float MinIntervalMs = 0f;

	public const float MaxIntervalMs = 3000f;

	public RunState CurrentState { get; private set; } = RunState.Idle;

	public string BasketStatusText { get; private set; } = "空闲";

	private Coroutine _loopCoroutine;

	// ---------- 老虎机 + Bonus 奖励关必中大奖 ----------
	public bool SlotAutoJackpotEnabled = true;

	public SlotRewardMode RewardMode = SlotRewardMode.MaxCoin;

	public string SlotStatusText { get; private set; } = "未打开老虎机";

	public bool LeverRunning { get; private set; }

	private Coroutine _leverCoroutine;

	// 兼容旧配置文件字段
	public int LeverInterval1Ms = 890;
	public int LeverInterval2Ms = 1665;
	public int LeverInterval3Ms = 1380;
	public const int MinLeverIntervalMs = 0;
	public const int MaxLeverIntervalMs = 10000;

	// ---------- 运行时反射缓存（无需在 .csproj 引用小游戏 DLL） ----------
	private Type _customSlotType;
	private Type _shooterType;
	private float _nextSlotScanTime;
	private readonly List<Component> _activeSlots = new List<Component>();
	private readonly HashSet<int> _manipulatedSlotIds = new HashSet<int>();

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
		// 默认热键 Alt+B：投篮开关
		if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.B))
		{
			ToggleEnabled();
		}
		// 默认热键 Alt+A：老虎机/奖励关一键必中拉一次
		if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.A))
		{
			TriggerLever();
		}

		if (SlotAutoJackpotEnabled)
		{
			TickSlotAutoJackpot();
		}
	}

	// ==================== 老虎机 + Bonus 奖励关反射核心 ====================

	private Type ResolveType(ref Type cache, string fullTypeName)
	{
		if (cache != null) return cache;
		foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			try
			{
				Type t = asm.GetType(fullTypeName);
				if (t != null)
				{
					cache = t;
					return cache;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private List<Component> RefreshActiveCustomSlots(bool force = false)
	{
		if (!force && Time.unscaledTime < _nextSlotScanTime)
		{
			// 剔除已销毁/隐藏的缓存项
			for (int i = _activeSlots.Count - 1; i >= 0; i--)
			{
				if (_activeSlots[i] == null || _activeSlots[i].gameObject == null || !_activeSlots[i].gameObject.activeInHierarchy)
				{
					_activeSlots.RemoveAt(i);
				}
			}
			if (_activeSlots.Count > 0) return _activeSlots;
		}
		_nextSlotScanTime = Time.unscaledTime + 0.35f;
		_activeSlots.Clear();

		Type slotType = ResolveType(ref _customSlotType, "CSFramework.CustomSlot");
		if (slotType == null) return _activeSlots;

		UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(slotType);
		if (found != null)
		{
			for (int i = 0; i < found.Length; i++)
			{
				if (found[i] is Component comp && comp.gameObject != null && comp.gameObject.activeInHierarchy)
				{
					_activeSlots.Add(comp);
				}
			}
		}
		return _activeSlots;
	}

	private bool IsBonusSlot(Component slot)
	{
		if (slot == null || slot.gameObject == null) return false;
		string n = slot.gameObject.name ?? "";
		if (n.IndexOf("bonus", StringComparison.OrdinalIgnoreCase) >= 0) return true;
		Transform p = slot.transform.parent;
		while (p != null)
		{
			if ((p.name ?? "").IndexOf("bonus", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			p = p.parent;
		}
		return false;
	}

	private void TickSlotAutoJackpot()
	{
		List<Component> slots = RefreshActiveCustomSlots(false);
		if (slots.Count == 0)
		{
			_manipulatedSlotIds.Clear();
			SlotStatusText = "未打开老虎机";
			return;
		}

		FieldInfo stateField = _customSlotType.GetField("state", BindingFlags.Public | BindingFlags.Instance);
		for (int i = 0; i < slots.Count; i++)
		{
			Component slot = slots[i];
			if (slot == null) continue;
			int id = slot.GetInstanceID();
			try
			{
				string stateName = stateField?.GetValue(slot)?.ToString() ?? "";
				if (stateName == "SpinStarting" || stateName == "Spinning")
				{
					if (!_manipulatedSlotIds.Contains(id))
					{
						// 若当前是 Bonus 奖励关子老虎机，强制锁定最高金币倍率，收益最大化
						SlotRewardMode modeToUse = IsBonusSlot(slot) ? SlotRewardMode.MaxCoin : RewardMode;
						if (ApplySlotJackpotManipulation(slot, modeToUse, out string info))
						{
							_manipulatedSlotIds.Add(id);
							SlotStatusText = (IsBonusSlot(slot) ? "[奖励关] " : "") + info;
						}
					}
				}
				else if (stateName == "Idle" || stateName == "Result" || stateName == "NotStarted")
				{
					_manipulatedSlotIds.Remove(id);
					if (!SlotStatusText.StartsWith("已锁定") && !SlotStatusText.StartsWith("[奖励关]"))
					{
						SlotStatusText = slots.Count > 1 ? "已就绪（主台+奖励关均已接管）" : "已就绪（点开始即自动锁定大奖）";
					}
				}
			}
			catch (Exception ex)
			{
				SlotStatusText = "检测异常: " + ex.Message;
			}
		}
	}

	private bool ApplySlotJackpotManipulation(Component slot, SlotRewardMode mode, out string statusInfo)
	{
		statusInfo = "未找到可用符号";
		if (slot == null) return false;

		object symMgr = _customSlotType.GetField("symbolManager", BindingFlags.Public | BindingFlags.Instance)?.GetValue(slot);
		if (symMgr == null) return false;

		Array symbols = symMgr.GetType().GetField("symbols", BindingFlags.Public | BindingFlags.Instance)?.GetValue(symMgr) as Array;
		if (symbols == null || symbols.Length == 0) return false;

		object bestNormal = null;
		int bestNormalPay = -1;
		object bestFreeSpin = null;
		int bestFreeSpinPay = -1;
		object bestBonus = null;
		int bestBonusPay = -1;

		foreach (object sym in symbols)
		{
			if (sym == null) continue;
			Type st = sym.GetType();
			string payTypeName = st.GetField("payType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(sym)?.ToString() ?? "";
			int[] pays = st.GetField("pays", BindingFlags.Public | BindingFlags.Instance)?.GetValue(sym) as int[];
			int maxPay = 0;
			if (pays != null)
			{
				for (int i = 0; i < pays.Length; i++)
				{
					if (pays[i] > maxPay) maxPay = pays[i];
				}
			}

			if (payTypeName == "Normal" && maxPay > bestNormalPay)
			{
				bestNormalPay = maxPay;
				bestNormal = sym;
			}
			else if (payTypeName == "FreesSpin" && maxPay >= bestFreeSpinPay)
			{
				bestFreeSpinPay = maxPay;
				bestFreeSpin = sym;
			}
			else if (payTypeName == "Bonus" && maxPay >= bestBonusPay)
			{
				bestBonusPay = maxPay;
				bestBonus = sym;
			}
		}

		object chosen;
		string modeLabel = "最高金币";
		int chosenPay = bestNormalPay;

		if (mode == SlotRewardMode.FreeSpin && bestFreeSpin != null)
		{
			chosen = bestFreeSpin;
			modeLabel = "免费旋转";
			chosenPay = bestFreeSpinPay;
		}
		else if (mode == SlotRewardMode.Bonus && bestBonus != null)
		{
			chosen = bestBonus;
			modeLabel = "奖励关";
			chosenPay = bestBonusPay;
		}
		else
		{
			chosen = bestNormal ?? bestFreeSpin ?? bestBonus;
		}

		if (chosen == null) return false;

		Array reels = _customSlotType.GetField("reels", BindingFlags.Public | BindingFlags.Instance)?.GetValue(slot) as Array;
		if (reels == null || reels.Length == 0) return false;

		int applied = 0;
		foreach (object reel in reels)
		{
			if (reel == null) continue;
			MethodInfo setManip = reel.GetType().GetMethod("SetManipulation", BindingFlags.Public | BindingFlags.Instance);
			if (setManip != null)
			{
				setManip.Invoke(reel, new object[] { chosen, 0, 0 });
				applied++;
			}
		}

		string symName = (chosen as UnityEngine.Object)?.name ?? "目标图标";
		statusInfo = $"已锁定[{modeLabel}]：{symName} (x{chosenPay}) · {applied}轮";
		return applied > 0;
	}

	// ==================== 投篮原生 Shooter.ShootBall 核心 ====================

	private Component GetActiveShooter()
	{
		Type shooterType = ResolveType(ref _shooterType, "Shooter");
		if (shooterType == null) return null;
		FieldInfo instField = shooterType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
		Component inst = instField?.GetValue(null) as Component;
		if (inst != null && inst.gameObject != null && inst.gameObject.activeInHierarchy)
		{
			return inst;
		}
		return null;
	}

	/// <summary>投篮热键和UI按钮共用的启停入口。</summary>
	public void ToggleEnabled()
	{
		if (Enabled) StopLoop();
		else StartLoop();
	}

	public void StartLoop()
	{
		if (Enabled || LeverRunning || EClass.pc == null) return;
		Enabled = true;
		BasketStatusText = "等待投篮机就绪...";
		_loopCoroutine = StartCoroutine(RunNativeBasketballLoop());
	}

	public void StopLoop()
	{
		Enabled = false;
		if (_loopCoroutine != null)
		{
			StopCoroutine(_loopCoroutine);
			_loopCoroutine = null;
		}
		CurrentState = RunState.Idle;
		BasketStatusText = "空闲";
	}

	private IEnumerator RunNativeBasketballLoop()
	{
		const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

		while (Enabled)
		{
			if (EClass.pc == null || EClass.pc.isDead || EClass.pc.stamina.value <= StopStaminaThreshold)
			{
				BasketStatusText = "已达体力停止阈值，自动停机";
				break;
			}

			Component shooter = GetActiveShooter();
			if (shooter == null)
			{
				CurrentState = RunState.Waiting;
				BasketStatusText = "未检测到投篮机，请先进入投篮小游戏";
				yield return new WaitForSecondsRealtime(0.4f);
				continue;
			}

			FieldInfo stateField = _shooterType.GetField("state", anyInst);
			MethodInfo shootMethod = _shooterType.GetMethod("ShootBall", anyInst);
			if (stateField == null || shootMethod == null)
			{
				BasketStatusText = "未找到 Shooter.ShootBall 接口";
				break;
			}

			string stName = stateField.GetValue(shooter)?.ToString() ?? "";
			if (stName != "Ready")
			{
				CurrentState = RunState.Waiting;
				BasketStatusText = "等待下一球装填...";
				yield return new WaitForSecondsRealtime(0.08f);
				continue;
			}

			// 直接原生调用 ShootBall(HoldSeconds)，不模拟鼠标，光标可自由移动
			CurrentState = RunState.Holding;
			float elapse = Mathf.Clamp(HoldSeconds, MinHoldSeconds, MaxHoldSeconds);
			try
			{
				shootMethod.Invoke(shooter, new object[] { elapse });
				BasketStatusText = $"原生投篮中（蓄力 {elapse:0.00}s · 不占鼠标）";
			}
			catch (Exception ex)
			{
				BasketStatusText = "投篮调用异常: " + ex.Message;
				break;
			}

			if (EClass.pc == null || EClass.pc.isDead || EClass.pc.stamina.value <= StopStaminaThreshold)
			{
				break;
			}

			CurrentState = RunState.Waiting;
			float waitSec = Mathf.Max(0.15f, Mathf.Clamp(IntervalMs, MinIntervalMs, MaxIntervalMs) / 1000f);
			yield return new WaitForSecondsRealtime(waitSec);
		}

		Enabled = false;
		CurrentState = RunState.Idle;
		_loopCoroutine = null;
	}

	/// <summary>老虎机/奖励关一键必中入口（Alt+A 或 UI 按钮）：优先操作奖励关子台，其次主老虎机。</summary>
	public void TriggerLever()
	{
		if (LeverRunning || Enabled || EClass.pc == null) return;
		_leverCoroutine = StartCoroutine(AutoSlotSpinCoroutine());
	}

	private IEnumerator AutoSlotSpinCoroutine()
	{
		LeverRunning = true;
		List<Component> slots = RefreshActiveCustomSlots(true);
		if (slots.Count == 0)
		{
			SlotStatusText = "请先打开老虎机界面再按 Alt+A";
			LeverRunning = false;
			_leverCoroutine = null;
			yield break;
		}

		// 若奖励关子台已打开，优先操作奖励关子台
		Component targetSlot = slots[0];
		for (int i = 0; i < slots.Count; i++)
		{
			if (IsBonusSlot(slots[i]))
			{
				targetSlot = slots[i];
				break;
			}
		}

		try
		{
			FieldInfo stateField = _customSlotType.GetField("state", BindingFlags.Public | BindingFlags.Instance);
			string stateName = stateField?.GetValue(targetSlot)?.ToString() ?? "";
			if (stateName == "Idle" || stateName == "NotStarted")
			{
				MethodInfo startSpin = _customSlotType.GetMethod("StartSpin", BindingFlags.Public | BindingFlags.Instance);
				if (startSpin != null)
				{
					ParameterInfo[] ps = startSpin.GetParameters();
					if (ps.Length == 1) startSpin.Invoke(targetSlot, new object[] { 0f });
					else startSpin.Invoke(targetSlot, null);
				}
			}
		}
		catch (Exception ex)
		{
			SlotStatusText = "启动旋转失败: " + ex.Message;
			LeverRunning = false;
			_leverCoroutine = null;
			yield break;
		}

		yield return new WaitForSecondsRealtime(0.12f);

		try
		{
			SlotRewardMode modeToUse = IsBonusSlot(targetSlot) ? SlotRewardMode.MaxCoin : RewardMode;
			if (ApplySlotJackpotManipulation(targetSlot, modeToUse, out string info))
			{
				_manipulatedSlotIds.Add(targetSlot.GetInstanceID());
				SlotStatusText = (IsBonusSlot(targetSlot) ? "[奖励关] " : "") + info;
			}
		}
		catch
		{
		}

		yield return new WaitForSecondsRealtime(0.25f);

		try
		{
			MethodInfo stopSpin = _customSlotType.GetMethod("StopSpin", BindingFlags.Public | BindingFlags.Instance);
			stopSpin?.Invoke(targetSlot, null);
		}
		catch
		{
		}

		LeverRunning = false;
		_leverCoroutine = null;
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
				streamWriter.WriteLine("SlotAutoJackpotEnabled=" + SlotAutoJackpotEnabled);
				streamWriter.WriteLine("RewardMode=" + RewardMode);
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
			if (!File.Exists(_configPath)) return;
			string[] array = File.ReadAllLines(_configPath);
			foreach (string text in array)
			{
				if (string.IsNullOrEmpty(text)) continue;
				string[] array2 = text.Split('=');
				if (array2.Length != 2) continue;
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
					case "SlotAutoJackpotEnabled":
						SlotAutoJackpotEnabled = bool.Parse(text3);
						break;
					case "RewardMode":
						RewardMode = (SlotRewardMode)Enum.Parse(typeof(SlotRewardMode), text3);
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
}