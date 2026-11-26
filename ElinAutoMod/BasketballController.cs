using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ElinAutoMod;

/// <summary>
/// 金玲赌场小游戏助手（极简可靠版，独立于 AutoController）：
/// 1. 老虎机：单开关锁定正中间中奖行最高金币图标，关闭立即 ClearManipulation 恢复原版随机。
/// 2. 投篮：严格按 Shooter.Update() 真实字节码执行 OnPlay(1) + ShootBall + 重置 state/ball，
///    并直接将球送入 GoalArea 篮筐正上方下落穿过触发器，100% 必进计分、涨投掷经验且不占鼠标。
/// </summary>
public class BasketballController : MonoBehaviour
{
	public enum RunState
	{
		Idle,
		Holding,
		Waiting
	}

	// ---------- 投篮参数 ----------
	public bool Enabled;

	public float IntervalMs = 350f;

	public int StopStaminaThreshold = 1;

	public const float MinIntervalMs = 150f;

	public const float MaxIntervalMs = 3000f;

	public RunState CurrentState { get; private set; } = RunState.Idle;

	public string BasketStatusText { get; private set; } = "空闲";

	private Coroutine _loopCoroutine;

	// ---------- 老虎机最高金币必中（单开关） ----------
	public bool SlotAutoJackpotEnabled = false;

	public string SlotStatusText { get; private set; } = "已关闭（正常随机）";

	public bool LeverRunning { get; private set; }

	private Coroutine _leverCoroutine;

	// ---------- 运行时反射缓存 ----------
	private Type _customSlotType;
	private Type _shooterType;
	private float _nextSlotScanTime;
	private readonly List<Component> _activeSlots = new List<Component>();
	private readonly HashSet<int> _manipulatedSlotIds = new HashSet<int>();
	private bool _hasAnyActiveManipulation;

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
		// Alt+B：投篮开关
		if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.B))
		{
			ToggleEnabled();
		}
		// Alt+A：老虎机一键拉一次
		if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.A))
		{
			TriggerLever();
		}

		if (SlotAutoJackpotEnabled)
		{
			TickSlotAutoJackpot();
		}
		else if (_hasAnyActiveManipulation)
		{
			ClearAllSlotManipulations();
		}
	}

	public void ToggleSlotJackpot()
	{
		SlotAutoJackpotEnabled = !SlotAutoJackpotEnabled;
		ClearAllSlotManipulations();
		SlotStatusText = SlotAutoJackpotEnabled ? "已开启（点开始即锁中间行最高金币）" : "已关闭（正常随机）";
		SaveConfig();
	}

	public void ClearAllSlotManipulations()
	{
		_manipulatedSlotIds.Clear();
		_hasAnyActiveManipulation = false;
		List<Component> slots = RefreshActiveCustomSlots(true);
		if (slots.Count == 0 || _customSlotType == null) return;

		MethodInfo clearSlotMethod = _customSlotType.GetMethod("ClearManipulation", BindingFlags.Public | BindingFlags.Instance);
		FieldInfo reelsField = _customSlotType.GetField("reels", BindingFlags.Public | BindingFlags.Instance);

		for (int i = 0; i < slots.Count; i++)
		{
			Component slot = slots[i];
			if (slot == null) continue;
			try
			{
				clearSlotMethod?.Invoke(slot, null);
				Array reels = reelsField?.GetValue(slot) as Array;
				if (reels != null)
				{
					foreach (object reel in reels)
					{
						if (reel == null) continue;
						reel.GetType().GetMethod("ClearManipulation", BindingFlags.Public | BindingFlags.Instance)?.Invoke(reel, null);
					}
				}
			}
			catch
			{
			}
		}
	}

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

	private int GetCenterRowOffset(Component slot)
	{
		try
		{
			object cfg = _customSlotType.GetField("config", BindingFlags.Public | BindingFlags.Instance)?.GetValue(slot);
			if (cfg != null)
			{
				object rowsObj = cfg.GetType().GetField("rows", BindingFlags.Public | BindingFlags.Instance)?.GetValue(cfg);
				if (rowsObj is int rows && rows >= 3)
				{
					return rows / 2;
				}
			}
		}
		catch
		{
		}
		return 1;
	}

	private void TickSlotAutoJackpot()
	{
		List<Component> slots = RefreshActiveCustomSlots(false);
		if (slots.Count == 0)
		{
			_manipulatedSlotIds.Clear();
			_hasAnyActiveManipulation = false;
			SlotStatusText = "已开启（等待打开老虎机）";
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
						if (ApplyMaxCoinManipulation(slot, out string info))
						{
							_manipulatedSlotIds.Add(id);
							_hasAnyActiveManipulation = true;
							SlotStatusText = info;
						}
					}
				}
				else if (stateName == "Idle" || stateName == "Result" || stateName == "NotStarted")
				{
					_manipulatedSlotIds.Remove(id);
				}
			}
			catch (Exception ex)
			{
				SlotStatusText = "检测异常: " + ex.Message;
			}
		}
	}

	private bool ApplyMaxCoinManipulation(Component slot, out string statusInfo)
	{
		statusInfo = "未找到最高金币图标";
		if (slot == null) return false;

		object symMgr = _customSlotType.GetField("symbolManager", BindingFlags.Public | BindingFlags.Instance)?.GetValue(slot);
		if (symMgr == null) return false;

		Array symbols = symMgr.GetType().GetField("symbols", BindingFlags.Public | BindingFlags.Instance)?.GetValue(symMgr) as Array;
		if (symbols == null || symbols.Length == 0) return false;

		object bestNormal = null;
		int bestPay = -1;
		foreach (object sym in symbols)
		{
			if (sym == null) continue;
			Type st = sym.GetType();
			string payTypeName = st.GetField("payType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(sym)?.ToString() ?? "";
			if (payTypeName != "Normal") continue;

			int[] pays = st.GetField("pays", BindingFlags.Public | BindingFlags.Instance)?.GetValue(sym) as int[];
			int maxPay = 0;
			if (pays != null)
			{
				for (int i = 0; i < pays.Length; i++)
				{
					if (pays[i] > maxPay) maxPay = pays[i];
				}
			}
			if (maxPay > bestPay)
			{
				bestPay = maxPay;
				bestNormal = sym;
			}
		}

		if (bestNormal == null) return false;

		Array reels = _customSlotType.GetField("reels", BindingFlags.Public | BindingFlags.Instance)?.GetValue(slot) as Array;
		if (reels == null || reels.Length == 0) return false;

		int centerRow = GetCenterRowOffset(slot);
		int applied = 0;
		foreach (object reel in reels)
		{
			if (reel == null) continue;
			MethodInfo setManip = reel.GetType().GetMethod("SetManipulation", BindingFlags.Public | BindingFlags.Instance);
			if (setManip != null)
			{
				setManip.Invoke(reel, new object[] { bestNormal, 0, centerRow });
				applied++;
			}
		}

		string symName = (bestNormal as UnityEngine.Object)?.name ?? "最高金币";
		statusInfo = $"已锁定最高金币：{symName} (x{bestPay})";
		return applied > 0;
	}

	/// <summary>热键 Alt+A：老虎机一键拉一次（若开启必中则自动锁最高金币并停轮）。</summary>
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
			SlotStatusText = "请先打开老虎机界面";
			LeverRunning = false;
			_leverCoroutine = null;
			yield break;
		}

		Component targetSlot = slots[0];
		if (!SlotAutoJackpotEnabled)
		{
			ClearAllSlotManipulations();
		}

		try
		{
			string stateName = _customSlotType.GetField("state", BindingFlags.Public | BindingFlags.Instance)?.GetValue(targetSlot)?.ToString() ?? "";
			if (stateName == "Idle" || stateName == "NotStarted")
			{
				MethodInfo startSpin = _customSlotType.GetMethod("StartSpin", BindingFlags.Public | BindingFlags.Instance);
				if (startSpin != null)
				{
					if (startSpin.GetParameters().Length == 1) startSpin.Invoke(targetSlot, new object[] { 0f });
					else startSpin.Invoke(targetSlot, null);
				}
			}
		}
		catch
		{
			LeverRunning = false;
			_leverCoroutine = null;
			yield break;
		}

		yield return new WaitForSecondsRealtime(0.12f);

		if (SlotAutoJackpotEnabled)
		{
			try
			{
				if (ApplyMaxCoinManipulation(targetSlot, out string info))
				{
					_manipulatedSlotIds.Add(targetSlot.GetInstanceID());
					_hasAnyActiveManipulation = true;
					SlotStatusText = info;
				}
			}
			catch
			{
			}
		}

		yield return new WaitForSecondsRealtime(0.25f);

		try
		{
			_customSlotType.GetMethod("StopSpin", BindingFlags.Public | BindingFlags.Instance)?.Invoke(targetSlot, null);
		}
		catch
		{
		}

		LeverRunning = false;
		_leverCoroutine = null;
	}

	// ==================== 投篮百发百中核心（直接送球穿过 GoalArea 触发器） ====================

	private Component GetActiveShooter()
	{
		Type shooterType = ResolveType(ref _shooterType, "Shooter");
		if (shooterType == null) return null;
		Component inst = shooterType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Component;
		if (inst != null && inst.gameObject != null && inst.gameObject.activeInHierarchy)
		{
			return inst;
		}
		return null;
	}

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
		_loopCoroutine = StartCoroutine(RunGuaranteedBasketballLoop());
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

	private IEnumerator RunGuaranteedBasketballLoop()
	{
		const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		const BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

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
				BasketStatusText = "请先进入投篮小游戏";
				yield return new WaitForSecondsRealtime(0.4f);
				continue;
			}

			FieldInfo stateField = _shooterType.GetField("state", anyInst);
			FieldInfo ballField = _shooterType.GetField("ball", anyInst);
			FieldInfo ballRbField = _shooterType.GetField("ballRigidbody", anyInst);
			FieldInfo goalField = _shooterType.GetField("goal", anyInst);
			FieldInfo guiField = _shooterType.GetField("gui", anyInst);
			FieldInfo gameField = _shooterType.GetField("game", anyStatic);
			MethodInfo shootMethod = _shooterType.GetMethod("ShootBall", anyInst);

			if (stateField == null || ballField == null || ballRbField == null || gameField == null || shootMethod == null)
			{
				BasketStatusText = "未找到 Shooter 接口";
				break;
			}

			string stName = stateField.GetValue(shooter)?.ToString() ?? "";
			Component curBall = ballField.GetValue(shooter) as Component;
			object ballRb = ballRbField.GetValue(shooter);
			if (stName != "Ready" || curBall == null || ballRb == null)
			{
				CurrentState = RunState.Waiting;
				yield return new WaitForSecondsRealtime(0.06f);
				continue;
			}

			CurrentState = RunState.Holding;
			try
			{
				// 1) 按 Shooter.Update 源码：激活球 isActive = true
				curBall.GetType().GetField("isActive", anyInst)?.SetValue(curBall, true);

				// 2) 按 Shooter.Update 源码：调用 Shooter.game.OnPlay(1)（扣1体力 + 投掷经验 +15）
				object gameObj = gameField.GetValue(null);
				if (gameObj != null)
				{
					var onPlay = gameObj.GetType().GetField("OnPlay", anyInst)?.GetValue(gameObj) as Func<int, bool>;
					var deactivate = gameObj.GetType().GetField("Deactivate", anyInst)?.GetValue(gameObj) as Action;
					if (onPlay != null && !onPlay(1))
					{
						deactivate?.Invoke();
						BasketStatusText = "体力不足，已退出投篮";
						break;
					}
				}

				// 3) 正常调用 ShootBall(0.4f) 触发音效、OnShoot 与状态清理
				shootMethod.Invoke(shooter, new object[] { 0.4f });

				// 4) 立刻将 state 置为 Charging(0) 且 ball 置为 null，防止 FixedUpdate 清零速度
				stateField.SetValue(shooter, Enum.ToObject(stateField.FieldType, 0));
				ballField.SetValue(shooter, null);

				// 5) 直接将球定位到 GoalArea 篮筐触发器正上方 0.25 单位并垂直下落穿过，100% 触发 GoalArea.OnTriggerEnter 计分！
				Transform hoopTransform = null;
				object guiObj = guiField?.GetValue(shooter);
				if (guiObj != null)
				{
					Component goalAreaComp = guiObj.GetType().GetField("goalArea", anyInst)?.GetValue(guiObj) as Component;
					if (goalAreaComp != null) hoopTransform = goalAreaComp.transform;
				}
				if (hoopTransform == null)
				{
					hoopTransform = goalField?.GetValue(shooter) as Transform;
				}
				if (hoopTransform != null)
				{
					curBall.transform.position = hoopTransform.position + new Vector3(0f, 0.25f, 0f);
					PropertyInfo velProp = ballRb.GetType().GetProperty("velocity", anyInst);
					velProp?.SetValue(ballRb, new Vector3(0f, -6f, 0f), null);
				}

				int curScore = (int)(_shooterType.GetField("score", anyInst)?.GetValue(shooter) ?? 0);
				BasketStatusText = $"百发百中投篮中（当前得分: {curScore}）";
			}
			catch (Exception ex)
			{
				BasketStatusText = "投篮异常: " + ex.Message;
				break;
			}

			if (EClass.pc == null || EClass.pc.isDead || EClass.pc.stamina.value <= StopStaminaThreshold)
			{
				BasketStatusText = "已达体力停止阈值，自动停机";
				break;
			}

			CurrentState = RunState.Waiting;
			float waitSec = Mathf.Clamp(IntervalMs, MinIntervalMs, MaxIntervalMs) / 1000f;
			yield return new WaitForSecondsRealtime(waitSec);
		}

		Enabled = false;
		CurrentState = RunState.Idle;
		_loopCoroutine = null;
	}

	public void SaveConfig()
	{
		try
		{
			Directory.CreateDirectory(_modRootPath);
			using (StreamWriter streamWriter = new StreamWriter(_configPath, false))
			{
				streamWriter.WriteLine("IntervalMs=" + IntervalMs.ToString(CultureInfo.InvariantCulture));
				streamWriter.WriteLine("StopStaminaThreshold=" + StopStaminaThreshold);
				streamWriter.WriteLine("SlotAutoJackpotEnabled=" + SlotAutoJackpotEnabled);
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
				string k = array2[0].Trim();
				string v = array2[1].Trim();
				try
				{
					switch (k)
					{
					case "IntervalMs":
						IntervalMs = float.Parse(v, CultureInfo.InvariantCulture);
						break;
					case "StopStaminaThreshold":
						StopStaminaThreshold = int.Parse(v);
						break;
					case "SlotAutoJackpotEnabled":
						SlotAutoJackpotEnabled = bool.Parse(v);
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