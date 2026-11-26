using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Algorithms;
using UnityEngine;

namespace ElinAutoMod;

public class AutoController : MonoBehaviour
{
	public enum State
	{
		Idle,
		Gathering,
		Combat,
		Kiting,
		Retreating,
		Harvesting,
		Picking
	}

	/// <summary>战斗索敌扫描模式：FullMap=扫描可行动范围内全部生物；Nearby=只扫描玩家周边半径内的生物。</summary>
	public enum CombatScanMode
	{
		FullMap,
		Nearby
	}

	/// <summary>采集顺序模式：NearestFirst=就近采集（默认，省脚步省生命）；CategoryOrder=按顺序：种子→采集→挖矿（减少切换工具）。</summary>
	public enum GatherOrderMode
	{
		NearestFirst,
		CategoryOrder
	}

	/// <summary>战斗目标选择优先级：Nearest=优先打近处（默认）；LowestHp=优先打血少（残血优先）。</summary>
	public enum CombatTargetPriority
	{
		Nearest,
		LowestHp
	}

	/// <summary>当近身无直接威胁时的全图探索优先级：MonsterFirst=怪优先（先全图寻怪再捡物）；PickupFirst=捡优先（先全图寻可拾取物再打怪）。</summary>
	public enum ExploreActionPriority
	{
		MonsterFirst,
		PickupFirst
	}

	/// <summary>地牢一键寻路目标：None=未开启，Exit=当层出口，NextFloor=下一层入口。</summary>
	public enum DungeonNavTarget
	{
		None,
		Exit,
		NextFloor
	}

	/// <summary>UI 采集设置页的视图子标签（仅用于 UI 翻页查看设置，实际运行按当前地图环境自动切换野外/地牢并叠加临时模块）。</summary>
	public enum GatherCategory
	{
		None = -1,
		Field = 0,
		Dungeon = 1,
		Temp = 2
	}

	/// <summary>专项材料收集的条目分类。</summary>
	public enum TargetItemCategory
	{
		Seed,
		Tree,
		Gather,
		Mine
	}

	/// <summary>供 UI 展示的专项收集候选项数据结构。</summary>
	public struct CatalogEntry
	{
		public string Key;
		public string Label;
		public TargetItemCategory Category;
		public int MapCount;
	}

	// 旧版写死路径。保留在这里仅用于"迁移旧配置"，不再作为实际存档路径使用。
	private const string LegacyConfigPath = "D:\\CSZccapp\\ElinAutoMod\\config.txt";

	// 新的根目录：跟随 mod DLL 所在位置（ElinAutoModConfig 子目录），换电脑/换安装路径都不会失效。
	private static readonly string _modRootPath = GetModRootPath();

	private static string _logPath => Path.Combine(_modRootPath, "debug.log");

	private static string _generalConfigPath => Path.Combine(_modRootPath, "config.txt");

	private static string _presetsDir => Path.Combine(_modRootPath, "TargetedPresets");

	private static string _basicPresetsDir => Path.Combine(_modRootPath, "BasicPresets");

	private static string _legacyPresetsDir => Path.Combine(_modRootPath, "Presets");

	private static string _activePresetPointerPath => Path.Combine(_modRootPath, "active_preset.txt");

	private static string _activeBasicPresetPointerPath => Path.Combine(_modRootPath, "active_basic_preset.txt");

	private const string DefaultPresetName = "默认";

	/// <summary>当前生效的专项预设名，切换/新建预设后会更新，并持久化到 active_preset.txt，下次启动自动沿用。</summary>
	public string ActivePresetName { get; private set; } = DefaultPresetName;

	/// <summary>当前生效的基础设置预设名（含快捷栏、阈值、更多全部参数），持久化到 active_basic_preset.txt。</summary>
	public string ActiveBasicPresetName { get; private set; } = DefaultPresetName;

	/// <summary>当前生效基础设置预设在磁盘中已保存的序列化快照（用于判断是否有未点击【保存】的改动）。</summary>
	private string _savedBasicPresetSnapshot = "";

	/// <summary>当前基础设置是否有尚未点击【保存】写入基础预设的改动。</summary>
	public bool IsBasicPresetDirty => !string.Equals(SerializeBasicPresetContent(), _savedBasicPresetSnapshot, StringComparison.Ordinal);

	/// <summary>当前生效专项预设在磁盘中已保存的 Key 快照（用于判断用户是否有未点击【保存】的临时勾选改动）。</summary>
	private HashSet<string> _savedTargetedGatherKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>当前生效专项预设在磁盘中已保存的显示名称快照。</summary>
	private Dictionary<string, string> _savedTargetedGatherLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>各专项预设已保存的条目数量缓存，供下拉列表直接显示。</summary>
	private Dictionary<string, int> _presetItemCountCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	/// <summary>当前专项勾选列表是否有尚未点击【保存】按钮写入预设的改动。</summary>
	public bool IsTargetedPresetDirty => !TargetedGatherKeys.SetEquals(_savedTargetedGatherKeys);

	public bool DebugMode;

	private static bool _debugEnabled = false;

	public State CurrentState;

	public bool Enabled;

	public int CombatSlot = -1;

	public int MiningSlot = -1;

	public int ChoppingSlot = -1;

	public int DisassembleSlot = -1;

	public int SickleSlot = -1;

	public int CurrentPage;

	public float KiteHpPercent = 0.5f;

	public float RetreatHpPercent = 0.15f;

	public int MaxStamina = 5;

	public bool AttackChicken;

	/// <summary>是否手动指定子区域坐标范围（默认 false = 自动实时读取 EClass._map.bounds 真实可行动边界，自动适配家园扩建、野外及海滩全可行动区，且绝不越出外圈装饰边界）。</summary>
	public bool UseCustomSearchBounds;

	public int SearchMinX = 36;

	public int SearchMaxX = 64;

	public int SearchMinZ = 36;

	public int SearchMaxZ = 64;

	// ---------- UI 采集设置页当前显示的子面板（仅作 UI 翻页，不再互斥屏蔽采集） ----------
	public GatherCategory gatherCategory = GatherCategory.Field;

	// ---------- 🌿 野外分类子项（支持多选并行，按目标自动切换对应工具，持久化到预设） ----------
	/// <summary>野外：砍树与杂物（树木 225、草药/藤蔓/蜘蛛网等 250，自动切砍树工具）。</summary>
	public bool FieldChop = true;

	/// <summary>野外：挖矿（地面/墙面附着的矿石、盐矿、晶簇、岩石等需采掘 220/230 的物体，自动切挖矿工具）。</summary>
	public bool FieldMine = true;

	/// <summary>野外：采作物/收种子（成熟植物 CanReapSeed，自动切镰刀）。</summary>
	public bool FieldCrops = true;

	// ---------- 🌊 深水区与游泳设置（持久化到预设） ----------
	/// <summary>直接无视深水区开关（穿戴潜水道具等场景下开启，彻底无视深水限制，把深水当陆地走）。</summary>
	public bool IgnoreDeepWater;

	/// <summary>游泳模式开关（开启后允许进入深水区行动，并实时检测氧气量，氧气降至阈值自动上岸恢复）。</summary>
	public bool SwimModeEnabled;

	/// <summary>游泳模式下触发自动上岸恢复的剩余氧气百分比阈值（默认 50%，对应 ConSuffocation.value >= 50）。</summary>
	public int SwimRetreatOxygenPercent = 50;

	// ---------- 🏰 地牢分类子项（支持多选并行，按目标材质自动切换对应工具，持久化到预设） ----------
	/// <summary>地牢：砍伐与杂物（树木/灌木 225、草药/藤蔓/蘑菇/蜘蛛网 250、骸骨残骸 mound 等，按原版材质自动切斧头或镐子）。</summary>
	public bool DungeonChop = true;

	/// <summary>地牢：采矿石与附着物（矿脉/晶簇/钟乳石/岩石/墙面铁皮等需采掘 220/230 的物体，不挖普通石墙，自动切挖矿工具）。</summary>
	public bool DungeonMineObjs = true;

	/// <summary>地牢：采作物/收种子（地牢内成熟植物/菌类 CanReapSeed，自动切镰刀）。</summary>
	public bool DungeonCrops = true;

	/// <summary>地牢内是否自动敲墙（挖掘地牢墙体 Block，按墙体材质自动切挖矿或砍树工具）。</summary>
	public bool DungeonMineWalls;

	/// <summary>地牢内自动敲墙的数量上限（0 表示不限数量，大于 0 时敲满 N 个墙自动停止脚本）。</summary>
	public int DungeonMineWallLimit = 20;

	/// <summary>当前地牢已敲墙计数（停止运行、切换地图或手动清零时复位）。</summary>
	public int DungeonMinedWallCount => _dungeonMinedWallCount;

	// ---------- ⚖️ 负重上限/超量停止设置（与地牢敲墙同级，也可全局生效，持久化到预设） ----------
	/// <summary>检测到达到负重上限/超量 N% 后是否自动停止脚本（默认关闭，避免平时负重练举重时刚启动就自动停）。</summary>
	public bool StopOnBurdenEnabled;

	/// <summary>自动停止脚本的负重百分比阈值（100 = 恰好达到负重上限 100%/超量 0%，120 = 超量 20%）。</summary>
	public int MaxBurdenPercent = 120;

	// ---------- ⚠️ 地牢地面陷阱避开与自动拆除设置（仅在地牢环境下生效，持久化到预设） ----------
	/// <summary>地牢中行走与寻路时是否默认避开地面已发现的陷阱（TraitTrap / 负面 TraitSwitch）。</summary>
	public bool AvoidTraps = true;

	/// <summary>地牢中遇到地面已发现的可拆除陷阱时，是否主动走到邻格尝试拆除陷阱。</summary>
	public bool DisarmTraps;

	// ---------- 🎣 钓鱼专用掉血保护设置（持久化到预设，默认开启） ----------
	/// <summary>启用钓鱼保护：勾选后，仅当检测到玩家处于钓鱼过程中（AI_Fish / ProgressFish / AIWork_Fish）时启动掉血检测并在掉血时停竿；不钓鱼或未勾选时绝不介入任何其他功能。</summary>
	public bool FishingProtectionEnabled = true;

	// ---------- ⏱️ 空闲/卡住超时自动停止设置（持久化到预设） ----------
	/// <summary>当角色空闲无目标或被地形（如深水区/隔墙）拦截停滞达到 N 秒时，是否自动停止脚本。</summary>
	public bool IdleAutoStopEnabled = true;

	/// <summary>空闲/停滞自动停止的秒数阈值（默认 10 秒）。</summary>
	public int IdleAutoStopSeconds = 10;

	// ---------- ⚡ 临时挖墙模块（独立附加模块，勾选后跟随常规采集一起执行；不持久化到配置，停止运行/切换预设/启动时自动归零防拆家） ----------
	/// <summary>临时：单一挖土墙（SourceMaterial.Row.category == "soil" 或海滩沙墙）。</summary>
	public bool TempMineSoil;

	/// <summary>临时：单一挖石墙（SourceMaterial.Row.category == "rock"）。</summary>
	public bool TempMineRock;

	/// <summary>临时：单一挖铁/金属墙（SourceMaterial.Row.category == "ore"）。</summary>
	public bool TempMineOre;

	/// <summary>临时：单一挖草/木墙（SourceMaterial.Row.category == "wood" || "grass"）。</summary>
	public bool TempMineWoodGrass;

	/// <summary>临时：无差别挖全部墙体（任意 HasBlock）。</summary>
	public bool TempMineAllWalls;

	// ---------- 🎯 单独/少量材料收集（专项采集）设置（持久化到预设） ----------
	/// <summary>专项材料收集总开关：勾选时只采集 TargetedGatherKeys 中选定的目标（可叠加临时挖墙）；不勾选时按默认全部采集规则运行。</summary>
	public bool TargetedGatherEnabled;

	/// <summary>已选定的专项材料收集 Key 集合（如 seed:wheat、tree_obj:tree_cedar、gather_thing:cobweb、mine_mat:copper 等）。</summary>
	public HashSet<string> TargetedGatherKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>已选定专项材料 Key 对应的显示名称缓存（持久化到配置，保证未进图时也能显示中文名）。</summary>
	public Dictionary<string, string> TargetedGatherLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	// ---------- 兼容属性 ----------
	public bool CollectCrops
	{
		get => FieldCrops;
		set => FieldCrops = value;
	}

	public bool MineWalls
	{
		get => TempMineAllWalls;
		set => TempMineAllWalls = value;
	}

	public bool MineWallsWithAxe
	{
		get => TempMineWoodGrass;
		set => TempMineWoodGrass = value;
	}

	/// <summary>兼容属性：当前是否处于地牢地图环境。</summary>
	public bool DungeonMode
	{
		get => IsInDungeonContext();
		set => gatherCategory = (value ? GatherCategory.Dungeon : GatherCategory.Field);
	}

	public bool autoPickEnabled = true;

	/// <summary>采集顺序模式：默认就近采集（NearestFirst），可切回按顺序：种子→采集→挖矿（CategoryOrder）。</summary>
	public GatherOrderMode gatherOrderMode = GatherOrderMode.NearestFirst;

	/// <summary>战斗目标选择偏好：默认优先打近处（Nearest），可切换为优先打血少（LowestHp）。</summary>
	public CombatTargetPriority combatTargetPriority = CombatTargetPriority.Nearest;

	/// <summary>近身无怪时的全图行动优先级：怪优先（MonsterFirst）或捡优先（PickupFirst）。</summary>
	public ExploreActionPriority explorePriority = ExploreActionPriority.MonsterFirst;

	/// <summary>近身自卫威胁半径（在此距离内的敌对生物无视“捡优先”，始终优先近身自卫反击）。</summary>
	public const float NearThreatRadius = 4.5f;

	/// <summary>战斗索敌范围：全图扫描 / 只看玩家周边 CombatScanRadius 格内。</summary>
	public CombatScanMode combatScanMode = CombatScanMode.FullMap;

	public int CombatScanRadius = 15;

	// ---------- 零分配安全寻路（避开所有明暗陷阱与禁入深水区）缓存 ----------
	private int _pfMapSize;
	private int _pfRunId;
	private int[] _pfVisitId;
	private int[] _pfClosedId;
	private int[] _pfGScore;
	private int[] _pfParentXZ;
	private int[] _pfHeapXZ;
	private int[] _pfHeapF;

	// ---------- 单源真实寻路距离场（Dijkstra）缓存：以真实步数/路程代替穿墙直线距离 ----------
	private int _dfMapSize;
	private int _dfStrictRunId;
	private int _dfPassHostileRunId;
	private int[] _dfStrictVisitId;
	private int[] _dfStrictG;
	private int[] _dfPassHostileVisitId;
	private int[] _dfPassHostileG;
	private float _dfCacheTime = -1f;
	private Map _dfCacheMap;
	private int _dfCachePcX = int.MinValue;
	private int _dfCachePcZ = int.MinValue;
	private bool _dfCacheAvoidTraps;
	private bool _dfCacheIgnoreDeepWater;
	private bool _dfCacheSwimMode;
	private bool _dfCacheSwimRecovering;

	private float _tickTimer;

	private float _tickInterval = 0.3f;

	private float _idleTickInterval = 0.8f;

	private Point _retreatTarget;

	private Point _lastShoreTarget;

	private bool _isSwimRecoveringOxygen;

	private Point _lastTrapStandTarget;

	private int _lastEquippedSlot = -1;

	private HashSet<string> _failedTargets = new HashSet<string>();

	private Dictionary<string, float> _unreachableGatherTargets = new Dictionary<string, float>();

	private Dictionary<string, int> _gatherFailCounts = new Dictionary<string, int>();

	private Dictionary<int, int> _pickupFailCounts = new Dictionary<int, int>();

	private Dictionary<int, int> _enemyFailCounts = new Dictionary<int, int>();

	private const int MaxFailedTargets = 2000;

	private Map _lastMap;

	private Point _lastGatherTarget;

	private int _gatherRetryCount;

	private const int MaxGatherRetry = 3;

	private Point _lastMineWallTarget;

	private int _dungeonMinedWallCount;

	private Thing targetPickup;

	private Dictionary<int, float> failedPickups = new Dictionary<int, float>();

	private float nextPickScanTime;

	private int _lastPickTargetUid = -1;

	private int _pickRetryCount;

	private float _lastPickDist = float.MaxValue;

	private Chara _currentCombatTarget;

	private Dictionary<int, float> _failedEnemies = new Dictionary<int, float>();

	private int _lastCombatTargetUid = -1;

	private int _combatMoveRetryCount;

	private float _lastCombatTargetDist = float.MaxValue;

	private Point _lastCombatStandTarget;

	private int _combatStuckTicks;

	private int _lastCombatPosX = int.MinValue;

	private int _lastCombatPosZ = int.MinValue;

	private int _lastPosX = int.MinValue;

	private int _lastPosZ = int.MinValue;

	private int _anchorPosX = int.MinValue;

	private int _anchorPosZ = int.MinValue;

	private float _stuckAutoTimer;

	private float _idleAutoTimer;

	private const int SlotHandOrCurrent = -2;

	private bool _legacyTargetKeysUpgraded;

	// ---------- 🎣 钓鱼掉血保护（仅在检测到玩家启动钓鱼、处于钓鱼过程中时激活，平时绝不介入） ----------
	private bool _fishingGuardActive;

	private int _fishingLastHp = int.MinValue;

	private int _lastNonFishingHp = int.MinValue;

	private Map _lastNonFishingMap;

	// ---------- 🧭 地牢一键寻路（当层出口 / 下一层入口）状态 ----------
	public DungeonNavTarget CurrentDungeonNav { get; private set; } = DungeonNavTarget.None;

	public bool IsDungeonNavigating => CurrentDungeonNav != DungeonNavTarget.None;

	private Point _lastDungeonNavStairStand;

	private int _dungeonNavRetryCount;

	public const float DungeonNavCombatRadius = 6.5f;

	public const float DungeonNavPickupRadius = 4.5f;

	/// <summary>实际生效的陷阱避开开关：地牢一键寻路期间默认强制避开陷阱。</summary>
	public bool EffectiveAvoidTraps => AvoidTraps || IsDungeonNavigating;

	public int GatherToolSlot
	{
		get
		{
			return MiningSlot;
		}
		set
		{
			MiningSlot = value;
		}
	}

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
		// 兜底：拿不到程序集路径时，退回旧目录
		return "D:\\CSZccapp\\ElinAutoMod";
	}

	private static string SanitizePresetName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return DefaultPresetName;
		}
		name = name.Trim();
		foreach (char c in Path.GetInvalidFileNameChars())
		{
			name = name.Replace(c, '_');
		}
		return string.IsNullOrWhiteSpace(name) ? DefaultPresetName : name;
	}

	private static string GetPresetFilePath(string presetName)
	{
		return Path.Combine(_presetsDir, SanitizePresetName(presetName) + ".txt");
	}

	private static string GetBasicPresetFilePath(string presetName)
	{
		return Path.Combine(_basicPresetsDir, SanitizePresetName(presetName) + ".txt");
	}

	private static void Log(string message)
	{
		if (!_debugEnabled)
		{
			return;
		}
		try
		{
			File.AppendAllText(_logPath, string.Format("[{0}] {1}\n", DateTime.Now.ToString("HH:mm:ss"), message));
		}
		catch
		{
		}
	}

	/// <summary>将所有临时挖墙开关复位为关闭（用于停止自动运行、切换预设、手动全关及游戏启动时，防止误拆家；UI 切页绝不调用）。</summary>
	public void ResetTempWallOptions()
	{
		TempMineSoil = false;
		TempMineRock = false;
		TempMineOre = false;
		TempMineWoodGrass = false;
		TempMineAllWalls = false;
	}

	public bool HasAnyTempWallEnabled()
	{
		return TempMineSoil || TempMineRock || TempMineOre || TempMineWoodGrass || TempMineAllWalls;
	}

	public void ResetDungeonMinedWallCount()
	{
		_dungeonMinedWallCount = 0;
		_lastMineWallTarget = null;
	}

	// ---------- 🎯 专项材料收集管理方法（同名/同类一键全包，跨地图通用，无冗余编码） ----------
	public bool IsTargetKeySelected(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return false;
		}
		UpgradeLegacyTargetedGatherKeysIfReady();
		if (TargetedGatherKeys.Contains(key))
		{
			return true;
		}
		string norm = NormalizeTargetKey(key, out _);
		return !string.IsNullOrEmpty(norm) && TargetedGatherKeys.Contains(norm);
	}

	public void ToggleTargetKey(string key, string label)
	{
		if (string.IsNullOrEmpty(key))
		{
			return;
		}
		UpgradeLegacyTargetedGatherKeysIfReady();
		string normKey = NormalizeTargetKey(key, out var normLabel);
		if (string.IsNullOrEmpty(normKey))
		{
			normKey = key;
		}
		string finalLabel = (!string.IsNullOrEmpty(label)) ? label : normLabel;

		if (TargetedGatherKeys.Contains(normKey))
		{
			TargetedGatherKeys.Remove(normKey);
			TargetedGatherLabels.Remove(normKey);
		}
		else
		{
			TargetedGatherKeys.Add(normKey);
			if (!string.IsNullOrEmpty(finalLabel))
			{
				TargetedGatherLabels[normKey] = finalLabel;
			}
		}
		// 同时清理可能残留的同义旧格式 Key
		if (!string.Equals(key, normKey, StringComparison.OrdinalIgnoreCase))
		{
			TargetedGatherKeys.Remove(key);
			TargetedGatherLabels.Remove(key);
		}
		_failedTargets.Clear();
		_unreachableGatherTargets.Clear();
		_gatherFailCounts.Clear();
		// 注意：不在勾选/取消勾选时自动保存专项预设，必须由用户点击 UI 上的【保存】按钮才写入预设文件，方便选错时直接切换预设或点回退恢复。
	}

	public void RemoveTargetKey(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return;
		}
		bool removed = TargetedGatherKeys.Remove(key);
		TargetedGatherLabels.Remove(key);
		string normKey = NormalizeTargetKey(key, out _);
		if (!string.IsNullOrEmpty(normKey) && TargetedGatherKeys.Remove(normKey))
		{
			TargetedGatherLabels.Remove(normKey);
			removed = true;
		}
		if (removed)
		{
			_failedTargets.Clear();
			_unreachableGatherTargets.Clear();
			_gatherFailCounts.Clear();
		}
	}

	public void ClearTargetedGatherKeys()
	{
		TargetedGatherKeys.Clear();
		TargetedGatherLabels.Clear();
		_failedTargets.Clear();
		_unreachableGatherTargets.Clear();
		_gatherFailCounts.Clear();
	}

	public string GetTargetKeyDisplayLabel(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return "";
		}
		UpgradeLegacyTargetedGatherKeysIfReady();
		string resolved = ResolveTargetKeyLabelFallback(key);
		if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, key, StringComparison.OrdinalIgnoreCase))
		{
			return resolved;
		}
		if (TargetedGatherLabels.TryGetValue(key, out var savedLabel) && !string.IsNullOrEmpty(savedLabel))
		{
			return StripInternalCodeSuffix(savedLabel);
		}
		return resolved;
	}

	/// <summary>去掉旧版遗留标签末尾的 (alias) 或 [材质·对象] 编码后缀，保持 UI 纯净。</summary>
	private static string StripInternalCodeSuffix(string label)
	{
		if (string.IsNullOrEmpty(label))
		{
			return "";
		}
		string s = label.Trim();
		int bracketIdx = s.LastIndexOf(" [", StringComparison.Ordinal);
		if (bracketIdx > 0 && s.EndsWith("]", StringComparison.Ordinal))
		{
			s = s.Substring(0, bracketIdx).TrimEnd();
		}
		int parenIdx = s.LastIndexOf(" (", StringComparison.Ordinal);
		if (parenIdx > 0 && s.EndsWith(")", StringComparison.Ordinal))
		{
			string inside = s.Substring(parenIdx + 2, s.Length - parenIdx - 3);
			bool isAsciiCode = !string.IsNullOrEmpty(inside);
			for (int i = 0; i < inside.Length; i++)
			{
				char c = inside[i];
				if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
				{
					isAsciiCode = false;
					break;
				}
			}
			if (isAsciiCode)
			{
				s = s.Substring(0, parenIdx).TrimEnd();
			}
		}
		return s;
	}

	/// <summary>当游戏数据源已就绪时，自动将旧版按编码保存的 Key（如 gather_obj:xxx、gather_thing:xxx、tree_obj:xxx、objmat:xxx:yyy）升级合并为同名/同类通用 Key。</summary>
	public void UpgradeLegacyTargetedGatherKeysIfReady()
	{
		if (_legacyTargetKeysUpgraded)
		{
			return;
		}
		if (Cell.objList == null || Cell.matList == null || EClass.sources?.things?.rows == null)
		{
			return;
		}
		_legacyTargetKeysUpgraded = true;
		bool wasClean = !IsTargetedPresetDirty;

		if (_savedTargetedGatherKeys.Count > 0)
		{
			HashSet<string> newSavedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> newSavedLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (string oldKey in _savedTargetedGatherKeys)
			{
				if (string.IsNullOrEmpty(oldKey))
				{
					continue;
				}
				string normKey = NormalizeTargetKey(oldKey, out var normLabel);
				if (string.IsNullOrEmpty(normKey))
				{
					normKey = oldKey;
				}
				newSavedKeys.Add(normKey);
				if (!string.IsNullOrEmpty(normLabel))
				{
					newSavedLabels[normKey] = normLabel;
				}
				else if (_savedTargetedGatherLabels.TryGetValue(oldKey, out var oldLabel) && !string.IsNullOrEmpty(oldLabel))
				{
					newSavedLabels[normKey] = StripInternalCodeSuffix(oldLabel);
				}
			}
			_savedTargetedGatherKeys = newSavedKeys;
			_savedTargetedGatherLabels = newSavedLabels;
		}

		if (TargetedGatherKeys.Count == 0)
		{
			return;
		}

		HashSet<string> newKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> newLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		bool changed = false;

		foreach (string oldKey in TargetedGatherKeys)
		{
			if (string.IsNullOrEmpty(oldKey))
			{
				continue;
			}
			string normKey = NormalizeTargetKey(oldKey, out var normLabel);
			if (string.IsNullOrEmpty(normKey))
			{
				normKey = oldKey;
			}
			if (!string.Equals(oldKey, normKey, StringComparison.OrdinalIgnoreCase))
			{
				changed = true;
			}
			newKeys.Add(normKey);
			if (!string.IsNullOrEmpty(normLabel))
			{
				newLabels[normKey] = normLabel;
			}
			else if (TargetedGatherLabels.TryGetValue(oldKey, out var oldLabel) && !string.IsNullOrEmpty(oldLabel))
			{
				newLabels[normKey] = StripInternalCodeSuffix(oldLabel);
			}
		}

		if (changed || newKeys.Count != TargetedGatherKeys.Count)
		{
			TargetedGatherKeys = newKeys;
			TargetedGatherLabels = newLabels;
			if (wasClean)
			{
				SaveCurrentTargetedPreset();
			}
		}
		else
		{
			foreach (KeyValuePair<string, string> kvp in newLabels)
			{
				TargetedGatherLabels[kvp.Key] = kvp.Value;
			}
		}
	}

	/// <summary>将任意旧版或新版 Key 归一化为"同名/同类一键全包"通用 Key，并生成干净的中文/本地化标签。</summary>
	private string NormalizeTargetKey(string rawKey, out string displayLabel)
	{
		displayLabel = "";
		if (string.IsNullOrEmpty(rawKey))
		{
			return "";
		}
		int colon = rawKey.IndexOf(':');
		if (colon <= 0 || colon >= rawKey.Length - 1)
		{
			displayLabel = rawKey;
			return rawKey;
		}
		string prefix = rawKey.Substring(0, colon).Trim().ToLowerInvariant();
		string rest = rawKey.Substring(colon + 1).Trim();
		if (string.IsNullOrEmpty(rest))
		{
			return rawKey;
		}

		switch (prefix)
		{
		case "seed":
		{
			if (rest == "*")
			{
				displayLabel = "🌾 [全部种子] 所有成熟可收种植物";
				return "seed:*";
			}
			SourceObj.Row r = FindObjRowByAlias(rest);
			string seedName = (r != null) ? GetCanonicalSeedName(r) : StripInternalCodeSuffix(rest);
			if (string.IsNullOrEmpty(seedName))
			{
				seedName = rest;
			}
			displayLabel = "🌾 种子: " + seedName;
			return "seed:" + seedName;
		}
		case "tree":
		case "tree_obj":
		case "tree_mat":
		{
			if (rest == "*")
			{
				displayLabel = "🪓 [全部树木] 不限树种全部砍伐";
				return "tree:*";
			}
			string treeName = "";
			if (prefix == "tree_obj" || prefix == "tree")
			{
				SourceObj.Row r2 = FindObjRowByAlias(rest);
				if (r2 != null)
				{
					treeName = GetCanonicalTreeObjName(r2);
				}
			}
			if (string.IsNullOrEmpty(treeName) && (prefix == "tree_mat" || prefix == "tree"))
			{
				SourceMaterial.Row m = FindMatRowByAlias(rest);
				if (m != null)
				{
					treeName = GetCanonicalTreeMatName(m);
				}
			}
			if (string.IsNullOrEmpty(treeName))
			{
				treeName = NormalizeTreeSpeciesName(StripInternalCodeSuffix(rest));
			}
			displayLabel = "🪓 树木: " + treeName;
			return "tree:" + treeName;
		}
		case "gather":
		case "gather_obj":
		case "gather_thing":
		{
			if (rest == "*")
			{
				displayLabel = "🕸️ [全部采集物] 所有蘑菇/蜘蛛网/花草/杂物";
				return "gather:*";
			}
			string gatherName = "";
			if (prefix == "gather_obj" || prefix == "gather")
			{
				SourceObj.Row r3 = FindObjRowByAlias(rest);
				if (r3 != null)
				{
					gatherName = GetCanonicalGatherObjName(r3);
				}
			}
			if (string.IsNullOrEmpty(gatherName) && (prefix == "gather_thing" || prefix == "gather"))
			{
				SourceThing.Row tr = FindThingRowById(rest);
				if (tr != null)
				{
					gatherName = GetCanonicalGatherThingName(tr);
				}
			}
			if (string.IsNullOrEmpty(gatherName))
			{
				if (rest.IndexOf("cobweb", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(rest, "web", StringComparison.OrdinalIgnoreCase) || rest.Contains("蜘蛛网") || rest.Contains("蛛网"))
				{
					gatherName = GetCanonicalCobwebName();
				}
				else if (rest.IndexOf("mushroom", StringComparison.OrdinalIgnoreCase) >= 0 || rest.IndexOf("kinoko", StringComparison.OrdinalIgnoreCase) >= 0 || rest.Contains("蘑菇") || rest.Contains("きのこ"))
				{
					gatherName = GetCanonicalMushroomName();
				}
				else
				{
					gatherName = CleanCatalogItemName(StripInternalCodeSuffix(rest));
				}
			}
			displayLabel = "🕸️ 采集: " + gatherName;
			return "gather:" + gatherName;
		}
		case "mine":
		{
			if (rest == "*")
			{
				displayLabel = "⛏️ [全部矿石/采掘物] 所有矿石/晶簇/宝石/岩石";
				return "mine:*";
			}
			break;
		}
		case "mine_mat":
		{
			SourceMaterial.Row m2 = FindMatRowByAlias(rest) ?? FindMatRowByName(rest);
			if (m2 != null && !string.IsNullOrEmpty(m2.alias))
			{
				string mName = SafeGetMatName(m2);
				string catTag = GetMineMaterialCategoryTag(m2.category);
				displayLabel = "⛏️ [" + catTag + "] " + mName;
				return "mine_mat:" + m2.alias;
			}
			displayLabel = "⛏️ 矿石材质: " + StripInternalCodeSuffix(rest);
			return "mine_mat:" + rest;
		}
		case "mine_obj":
		{
			SourceObj.Row r4 = FindObjRowByAlias(rest);
			string mineObjName = (r4 != null) ? GetCanonicalMineObjName(r4) : CleanCatalogItemName(StripInternalCodeSuffix(rest));
			if (string.IsNullOrEmpty(mineObjName))
			{
				mineObjName = rest;
			}
			displayLabel = "🪨 [采掘物类型] " + mineObjName;
			return "mine_obj:" + mineObjName;
		}
		case "wall_mat":
		{
			SourceMaterial.Row m3 = FindMatRowByAlias(rest) ?? FindMatRowByName(rest);
			string wName = (m3 != null) ? SafeGetMatName(m3) : StripInternalCodeSuffix(rest);
			string wAlias = (m3 != null && !string.IsNullOrEmpty(m3.alias)) ? m3.alias : rest;
			displayLabel = "🧱 墙体材质: " + wName;
			return "wall_mat:" + wAlias;
		}
		case "objmat":
		{
			// 旧版 objmat:objAlias:matAlias 自动升级为按材质或通用对象通采，彻底告别换图失效
			int colon2 = rest.IndexOf(':');
			if (colon2 > 0 && colon2 < rest.Length - 1)
			{
				string oAlias = rest.Substring(0, colon2);
				string mAlias = rest.Substring(colon2 + 1);
				SourceObj.Row oRow = FindObjRowByAlias(oAlias);
				SourceMaterial.Row mRow = FindMatRowByAlias(mAlias);
				if (oRow != null && IsGatherObjRow(oRow))
				{
					string gName = GetCanonicalGatherObjName(oRow);
					displayLabel = "🕸️ 采集: " + gName;
					return "gather:" + gName;
				}
				if (oRow != null && IsTreeObjRow(oRow))
				{
					string tName = (mRow != null) ? GetCanonicalTreeMatName(mRow) : GetCanonicalTreeObjName(oRow);
					displayLabel = "🪓 树木: " + tName;
					return "tree:" + tName;
				}
				if (mRow != null && !string.IsNullOrEmpty(mRow.alias))
				{
					string mName = SafeGetMatName(mRow);
					string catTag = GetMineMaterialCategoryTag(mRow.category);
					displayLabel = "⛏️ [" + catTag + "] " + mName;
					return "mine_mat:" + mRow.alias;
				}
			}
			break;
		}
		}

		displayLabel = StripInternalCodeSuffix(rawKey);
		return rawKey;
	}

	private string ResolveTargetKeyLabelFallback(string key)
	{
		try
		{
			NormalizeTargetKey(key, out var label);
			if (!string.IsNullOrEmpty(label))
			{
				return label;
			}
		}
		catch
		{
		}
		return key;
	}

	private static string GetMineMaterialCategoryTag(string cat)
	{
		return (cat == "ore") ? "金属矿" : ((cat == "gem") ? "宝石矿" : ((cat == "crystal") ? "水晶矿" : ((cat == "rock") ? "岩石" : ((cat == "soil") ? "土/沙" : "骨/其他"))));
	}

	private static string CleanCatalogItemName(string raw)
	{
		if (string.IsNullOrEmpty(raw))
		{
			return "";
		}
		return StripInternalCodeSuffix(raw).Trim();
	}

	private static bool HasGrowthType(SourceObj.Row r, string growthTypeName)
	{
		if (r == null || r._growth == null || r._growth.Length == 0 || string.IsNullOrEmpty(r._growth[0]))
		{
			return false;
		}
		return string.Equals(r._growth[0], growthTypeName, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>判定 SourceObj.Row 是否为蘑菇（GrowSystemKinoko 或名称/别名为蘑菇）。
	/// 严格基于 GrowSystemKinoko.cs：所有 GrowSystemKinoko 挖掘时均调用 ThingGen.CreateFromCategory("mushroom")。</summary>
	private bool IsMushroomObjRow(SourceObj.Row r)
	{
		if (r == null || r.id == 0)
		{
			return false;
		}
		if (r.growth is GrowSystemKinoko || HasGrowthType(r, "Kinoko"))
		{
			return true;
		}
		string alias = r.alias ?? "";
		if (alias.IndexOf("mushroom", StringComparison.OrdinalIgnoreCase) >= 0 || alias.IndexOf("kinoko", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		string name = SafeGetObjName(r);
		return name.Contains("蘑菇") || name.Contains("きのこ") || string.Equals(name, "mushroom", StringComparison.OrdinalIgnoreCase);
	}

	private bool IsCobwebObjRow(SourceObj.Row r)
	{
		if (r == null || r.id == 0)
		{
			return false;
		}
		string alias = r.alias ?? "";
		if (alias.IndexOf("cobweb", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(alias, "web", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string name = SafeGetObjName(r);
		return name.Contains("蜘蛛网") || name.Contains("蛛网") || name.Contains("クモの巣") || string.Equals(name, "cobweb", StringComparison.OrdinalIgnoreCase);
	}

	private bool IsCobwebThingRow(SourceThing.Row tr)
	{
		if (tr == null || string.IsNullOrEmpty(tr.id))
		{
			return false;
		}
		if (tr.trait != null && tr.trait.Length > 0 && string.Equals(tr.trait[0], "CobWeb", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (tr.id.IndexOf("cobweb", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(tr.id, "web", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string name = SafeGetThingRowName(tr);
		return name.Contains("蜘蛛网") || name.Contains("蛛网") || name.Contains("クモの巣") || string.Equals(name, "cobweb", StringComparison.OrdinalIgnoreCase);
	}

	private bool IsHerbObjRow(SourceObj.Row r)
	{
		if (r == null || r.id == 0)
		{
			return false;
		}
		return r.growth is GrowSystemHerb || HasGrowthType(r, "Herb");
	}

	private bool IsFlowerObjRow(SourceObj.Row r)
	{
		if (r == null || r.id == 0)
		{
			return false;
		}
		if (r.growth is GrowSystemFlower || HasGrowthType(r, "Flower"))
		{
			return true;
		}
		string name = CleanCatalogItemName(SafeGetObjName(r));
		return name == "花" || name == "お花" || name == "鲜花" || name == "野花" || string.Equals(name, "flower", StringComparison.OrdinalIgnoreCase);
	}

	private bool IsWeedObjRow(SourceObj.Row r)
	{
		if (r == null || r.id == 0)
		{
			return false;
		}
		if (r.growth is GrowSystemWeed || HasGrowthType(r, "Weed"))
		{
			return true;
		}
		string name = CleanCatalogItemName(SafeGetObjName(r));
		return name == "杂草" || name == "雑草" || string.Equals(name, "weed", StringComparison.OrdinalIgnoreCase);
	}

	private string GetCanonicalMushroomName()
	{
		if (Cell.objList != null)
		{
			for (int i = 0; i < Cell.objList.Count; i++)
			{
				SourceObj.Row r = Cell.objList[i];
				if (r != null && (r.growth is GrowSystemKinoko || HasGrowthType(r, "Kinoko")))
				{
					string n = CleanCatalogItemName(SafeGetObjName(r));
					if (!string.IsNullOrEmpty(n))
					{
						return n;
					}
				}
			}
		}
		return "蘑菇";
	}

	private string GetCanonicalCobwebName()
	{
		if (EClass.sources?.things?.rows != null)
		{
			List<SourceThing.Row> rows = EClass.sources.things.rows;
			for (int i = 0; i < rows.Count; i++)
			{
				SourceThing.Row tr = rows[i];
				if (tr != null && tr.trait != null && tr.trait.Length > 0 && tr.trait[0] == "CobWeb")
				{
					string n = CleanCatalogItemName(SafeGetThingRowName(tr));
					if (!string.IsNullOrEmpty(n))
					{
						return n;
					}
				}
			}
		}
		return "蜘蛛网";
	}

	private string GetCanonicalHerbName()
	{
		if (Cell.objList != null)
		{
			for (int i = 0; i < Cell.objList.Count; i++)
			{
				SourceObj.Row r = Cell.objList[i];
				if (r != null && (r.growth is GrowSystemHerb || HasGrowthType(r, "Herb")))
				{
					string n = CleanCatalogItemName(SafeGetObjName(r));
					if (!string.IsNullOrEmpty(n))
					{
						return n;
					}
				}
			}
		}
		return "草药";
	}

	private string GetCanonicalFlowerName()
	{
		if (Cell.objList != null)
		{
			for (int i = 0; i < Cell.objList.Count; i++)
			{
				SourceObj.Row r = Cell.objList[i];
				if (r != null && (r.growth is GrowSystemFlower || HasGrowthType(r, "Flower")))
				{
					string n = CleanCatalogItemName(SafeGetObjName(r));
					if (!string.IsNullOrEmpty(n))
					{
						return n;
					}
				}
			}
		}
		return "花";
	}

	private string GetCanonicalWeedName()
	{
		if (Cell.objList != null)
		{
			for (int i = 0; i < Cell.objList.Count; i++)
			{
				SourceObj.Row r = Cell.objList[i];
				if (r != null && (r.growth is GrowSystemWeed || HasGrowthType(r, "Weed")))
				{
					string n = CleanCatalogItemName(SafeGetObjName(r));
					if (!string.IsNullOrEmpty(n))
					{
						return n;
					}
				}
			}
		}
		return "杂草";
	}

	/// <summary>获取植物种子的统一归并名称（同名不同编码、蘑菇/花/草药/杂草全部自动合一）。</summary>
	private string GetCanonicalSeedName(SourceObj.Row r)
	{
		if (r == null || r.id == 0)
		{
			return "";
		}
		if (IsMushroomObjRow(r))
		{
			return GetCanonicalMushroomName();
		}
		if (IsHerbObjRow(r))
		{
			return GetCanonicalHerbName();
		}
		if (IsFlowerObjRow(r))
		{
			return GetCanonicalFlowerName();
		}
		if (IsWeedObjRow(r))
		{
			return GetCanonicalWeedName();
		}
		return CleanCatalogItemName(SafeGetObjName(r));
	}

	/// <summary>将树名与木材名归一化（例如"白桦木"/"白桦之木"/"白桦树"可与"白桦"互通匹配）。</summary>
	private static string NormalizeTreeSpeciesName(string raw)
	{
		if (string.IsNullOrEmpty(raw))
		{
			return "";
		}
		string s = CleanCatalogItemName(raw);
		if (s.Length > 2 && s.EndsWith("之木", StringComparison.Ordinal))
		{
			s = s.Substring(0, s.Length - 2).Trim();
		}
		else if (s.Length > 2 && s.EndsWith("の木", StringComparison.Ordinal))
		{
			s = s.Substring(0, s.Length - 2).Trim();
		}
		return s;
	}

	private static string GetTreeSpeciesRoot(string name)
	{
		string s = NormalizeTreeSpeciesName(name);
		if (s.Length >= 2 && (s.EndsWith("树", StringComparison.Ordinal) || s.EndsWith("木", StringComparison.Ordinal)))
		{
			return s.Substring(0, s.Length - 1);
		}
		return s;
	}

	private string GetCanonicalTreeObjName(SourceObj.Row r)
	{
		if (r == null || r.id == 0)
		{
			return "";
		}
		return NormalizeTreeSpeciesName(SafeGetObjName(r));
	}

	private string GetCanonicalTreeMatName(SourceMaterial.Row m)
	{
		if (m == null || m.id == 0)
		{
			return "";
		}
		return NormalizeTreeSpeciesName(SafeGetMatName(m));
	}

	private string GetCanonicalGatherObjName(SourceObj.Row r)
	{
		if (r == null || r.id == 0)
		{
			return "";
		}
		if (IsMushroomObjRow(r))
		{
			return GetCanonicalMushroomName();
		}
		if (IsCobwebObjRow(r))
		{
			return GetCanonicalCobwebName();
		}
		if (IsHerbObjRow(r))
		{
			return GetCanonicalHerbName();
		}
		if (IsFlowerObjRow(r))
		{
			return GetCanonicalFlowerName();
		}
		if (IsWeedObjRow(r))
		{
			return GetCanonicalWeedName();
		}
		return CleanCatalogItemName(SafeGetObjName(r));
	}

	private string GetCanonicalGatherThingName(SourceThing.Row tr, Trait runtimeTrait = null)
	{
		if (runtimeTrait is TraitCobWeb || IsCobwebThingRow(tr))
		{
			return GetCanonicalCobwebName();
		}
		if (tr == null)
		{
			return "";
		}
		string name = CleanCatalogItemName(SafeGetThingRowName(tr));
		if (tr.category == "mushroom" || name.Contains("蘑菇") || name.Contains("きのこ") || string.Equals(name, "mushroom", StringComparison.OrdinalIgnoreCase))
		{
			return GetCanonicalMushroomName();
		}
		return name;
	}

	private string GetCanonicalMineObjName(SourceObj.Row r)
	{
		if (r == null || r.id == 0)
		{
			return "";
		}
		return CleanCatalogItemName(SafeGetObjName(r));
	}

	/// <summary>严格按照 SourceObj.cs 与 GrowSystem.cs 判定某 Obj 是否属于树木（GrowSystemTree.IsTree == true，或无生长系统的纯木本/枯木对象）。
	/// 绝不会把蘑菇(GrowSystemKinoko)、花(GrowSystemFlower)、草药(GrowSystemHerb)、浆果(GrowSystemBerry)、农作物等非树木植物误判为树！</summary>
	private bool IsTreeObjRow(SourceObj.Row r)
	{
		if (r == null || r.id == 0)
		{
			return false;
		}
		if (r.growth != null)
		{
			return r.growth.IsTree;
		}
		if (r._growth != null && r._growth.Length > 0 && !string.IsNullOrEmpty(r._growth[0]))
		{
			return r._growth[0].StartsWith("Tree", StringComparison.OrdinalIgnoreCase);
		}
		if (r.reqHarvest == null || r.reqHarvest.Length == 0 || string.IsNullOrEmpty(r.reqHarvest[0]))
		{
			return false;
		}
		int ele = ResolveHarvestElementByRawKey(r.reqHarvest[0]);
		if (ele != 225)
		{
			return false;
		}
		string alias = (r.alias ?? "").ToLowerInvariant();
		string name = SafeGetObjName(r);
		string nameJp = r.name_JP ?? "";
		return alias.Contains("tree") || alias.Contains("palm") || alias.Contains("bamboo") || alias.Contains("wood")
			|| name.Contains("树") || name.Contains("木") || name.Contains("竹")
			|| nameJp.Contains("木") || nameJp.Contains("竹") || nameJp.Contains("ヤシ");
	}

	/// <summary>严格判定某 Obj 是否属于矿石/岩石/采掘物（非生长植物，且 reqHarvest 为 220 mining 或 230 digging，排除 mound 残骸）。</summary>
	private bool IsMineObjRow(SourceObj.Row r)
	{
		if (r == null || r.id == 0 || r.HasGrowth || r.growth != null)
		{
			return false;
		}
		if (string.Equals(r.alias, "mound", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (r.reqHarvest == null || r.reqHarvest.Length == 0 || string.IsNullOrEmpty(r.reqHarvest[0]))
		{
			return false;
		}
		int ele = ResolveHarvestElementByRawKey(r.reqHarvest[0]);
		return ele == 220 || ele == 230;
	}

	/// <summary>严格判定某 Obj 是否属于【🕸️采集】分类（含所有蘑菇 GrowSystemKinoko、草药 GrowSystemHerb、花 GrowSystemFlower、浆果 GrowSystemBerry、杂草 GrowSystemWeed、其他非树植物、蜘蛛网、藤蔓、残骸 mound 等）。</summary>
	private bool IsGatherObjRow(SourceObj.Row r)
	{
		if (r == null || r.id == 0)
		{
			return false;
		}
		if (IsTreeObjRow(r) || IsMineObjRow(r))
		{
			return false;
		}
		if (string.Equals(r.alias, "mound", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (r.growth != null || r.HasGrowth || (r._growth != null && r._growth.Length > 0 && !string.IsNullOrEmpty(r._growth[0])))
		{
			return r.reqHarvest != null && r.reqHarvest.Length > 0 && !string.IsNullOrEmpty(r.reqHarvest[0]);
		}
		if (r.reqHarvest == null || r.reqHarvest.Length == 0 || string.IsNullOrEmpty(r.reqHarvest[0]))
		{
			return false;
		}
		int ele = ResolveHarvestElementByRawKey(r.reqHarvest[0]);
		return ele == 250 || ele == 225;
	}

	private SourceObj.Row FindObjRowByAlias(string alias)
	{
		if (string.IsNullOrEmpty(alias) || Cell.objList == null)
		{
			return null;
		}
		for (int i = 0; i < Cell.objList.Count; i++)
		{
			SourceObj.Row r = Cell.objList[i];
			if (r != null && string.Equals(r.alias, alias, StringComparison.OrdinalIgnoreCase))
			{
				return r;
			}
		}
		return null;
	}

	private SourceMaterial.Row FindMatRowByAlias(string alias)
	{
		if (string.IsNullOrEmpty(alias) || Cell.matList == null)
		{
			return null;
		}
		for (int i = 0; i < Cell.matList.Count; i++)
		{
			SourceMaterial.Row m = Cell.matList[i];
			if (m != null && string.Equals(m.alias, alias, StringComparison.OrdinalIgnoreCase))
			{
				return m;
			}
		}
		return null;
	}

	private SourceMaterial.Row FindMatRowByName(string name)
	{
		if (string.IsNullOrEmpty(name) || Cell.matList == null)
		{
			return null;
		}
		string clean = CleanCatalogItemName(name);
		for (int i = 0; i < Cell.matList.Count; i++)
		{
			SourceMaterial.Row m = Cell.matList[i];
			if (m != null && m.id != 0)
			{
				string mName = CleanCatalogItemName(SafeGetMatName(m));
				if (string.Equals(mName, clean, StringComparison.OrdinalIgnoreCase))
				{
					return m;
				}
			}
		}
		return null;
	}

	private SourceThing.Row FindThingRowById(string id)
	{
		if (string.IsNullOrEmpty(id) || EClass.sources?.things?.rows == null)
		{
			return null;
		}
		List<SourceThing.Row> rows = EClass.sources.things.rows;
		for (int i = 0; i < rows.Count; i++)
		{
			SourceThing.Row r = rows[i];
			if (r != null && string.Equals(r.id, id, StringComparison.OrdinalIgnoreCase))
			{
				return r;
			}
		}
		return null;
	}

	/// <summary>扫描当前地图内所有可收种植物、树木、采集物（蘑菇/蜘蛛网/草药/花/残骸等）和矿石，
	/// 严格按"同名/同类一键全包"归并统计数量，选中任意一项后在所有地图永久通用！</summary>
	public List<CatalogEntry> ScanCurrentMapGatherCatalog()
	{
		List<CatalogEntry> list = new List<CatalogEntry>();
		if (EClass._map == null || EClass.pc == null)
		{
			return list;
		}
		UpgradeLegacyTargetedGatherKeysIfReady();
		Dictionary<string, CatalogEntry> dict = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
		GetGatherScanBounds(out var minX, out var maxX, out var minZ, out var maxZ);

		Cell prevGrowCell = GrowSystem.cell;
		try
		{
			for (int i = minX; i <= maxX; i++)
			{
				for (int j = minZ; j <= maxZ; j++)
				{
					Point p = new Point(i, j);
					if (!p.IsValid || !p.IsInBounds)
					{
						continue;
					}
					Cell cell = p.cell;
					if (cell == null || cell.outOfBounds)
					{
						continue;
					}

					// 1. 种子（成熟可收割植物）— 按植物基础名归并
					if (p.HasObj && p.sourceObj != null && p.sourceObj.id != 0 && cell.CanReapSeed())
					{
						string seedName = GetCanonicalSeedName(p.sourceObj);
						if (!string.IsNullOrEmpty(seedName))
						{
							string seedKey = "seed:" + seedName;
							AddOrIncrementCatalogEntry(dict, seedKey, "🌾 种子: " + seedName, TargetItemCategory.Seed);
						}
					}

					// 2. 地面/附着 Obj（按真实类型归入 树木 / 采集 / 矿石，同名/同类彻底合并）
					if (TryGetObjHarvestInfo(p, out var objEle) && p.sourceObj != null && p.sourceObj.id != 0)
					{
						SourceObj.Row sObj = p.sourceObj;
						SourceMaterial.Row matObj = cell.matObj;

						if (IsTreeObjRow(sObj))
						{
							string treeName = GetBestTreeDisplayName(sObj, matObj);
							if (!string.IsNullOrEmpty(treeName))
							{
								string treeKey = "tree:" + treeName;
								AddOrIncrementCatalogEntry(dict, treeKey, "🪓 树木: " + treeName, TargetItemCategory.Tree);
							}
						}
						else if (IsMineObjRow(sObj) || ((objEle == 220 || objEle == 230) && !IsGatherObjRow(sObj)))
						{
							if (matObj != null && matObj.id != 0 && !string.IsNullOrEmpty(matObj.alias))
							{
								string matName = SafeGetMatName(matObj);
								string catTag = GetMineMaterialCategoryTag(matObj.category);
								string matKey = "mine_mat:" + matObj.alias;
								AddOrIncrementCatalogEntry(dict, matKey, "⛏️ [" + catTag + "] " + matName, TargetItemCategory.Mine);
							}
							string mineObjName = GetCanonicalMineObjName(sObj);
							if (!string.IsNullOrEmpty(mineObjName))
							{
								string mineObjKey = "mine_obj:" + mineObjName;
								AddOrIncrementCatalogEntry(dict, mineObjKey, "🪨 [采掘物类型] " + mineObjName, TargetItemCategory.Mine);
							}
						}
						else
						{
							string gatherName = GetCanonicalGatherObjName(sObj);
							if (!string.IsNullOrEmpty(gatherName))
							{
								string gatherKey = "gather:" + gatherName;
								AddOrIncrementCatalogEntry(dict, gatherKey, "🕸️ 采集: " + gatherName, TargetItemCategory.Gather);
							}
						}
					}

					// 3. 格内可采集 Thing（如蜘蛛网 TraitCobWeb 等）— 与 Obj 共用统一 gather:<名称> Key
					if (p.HasThing && p.Things != null)
					{
						List<Thing> things = p.Things;
						for (int k = 0; k < things.Count; k++)
						{
							Thing t = things[k];
							if (t == null || ((Card)t).isDestroyed || ((Card)t).isHidden || ((Card)t).isMasked || t.trait == null)
							{
								continue;
							}
							string reqHarvest = t.trait.ReqHarvest;
							if (string.IsNullOrEmpty(reqHarvest))
							{
								continue;
							}
							string[] raw = reqHarvest.Split(',');
							if (raw == null || raw.Length == 0 || string.IsNullOrEmpty(raw[0]))
							{
								continue;
							}
							int ele = ResolveHarvestElement(p, raw[0], checkCellGrowth: false);
							if (ele <= 0 || (ele == 250 && !t.trait.CanHarvest))
							{
								continue;
							}
							if (ele == 220 || ele == 230)
							{
								SourceMaterial.Row tMat = t.material;
								if (tMat != null && tMat.id != 0 && !string.IsNullOrEmpty(tMat.alias))
								{
									string mName = SafeGetMatName(tMat);
									string catTag = GetMineMaterialCategoryTag(tMat.category);
									AddOrIncrementCatalogEntry(dict, "mine_mat:" + tMat.alias, "⛏️ [" + catTag + "] " + mName, TargetItemCategory.Mine);
								}
								string mThingName = CleanCatalogItemName(SafeGetThingName(t));
								if (!string.IsNullOrEmpty(mThingName))
								{
									AddOrIncrementCatalogEntry(dict, "mine_obj:" + mThingName, "🪨 [采掘物类型] " + mThingName, TargetItemCategory.Mine);
								}
							}
							else if (ele == 225)
							{
								string tTreeName = NormalizeTreeSpeciesName(SafeGetThingName(t));
								if (!string.IsNullOrEmpty(tTreeName))
								{
									AddOrIncrementCatalogEntry(dict, "tree:" + tTreeName, "🪓 树木: " + tTreeName, TargetItemCategory.Tree);
								}
							}
							else
							{
								string gName = GetCanonicalGatherThingName(t.source, t.trait);
								if (string.IsNullOrEmpty(gName))
								{
									gName = CleanCatalogItemName(SafeGetThingName(t));
								}
								if (!string.IsNullOrEmpty(gName))
								{
									AddOrIncrementCatalogEntry(dict, "gather:" + gName, "🕸️ 采集: " + gName, TargetItemCategory.Gather);
								}
							}
						}
					}
				}
			}
		}
		finally
		{
			GrowSystem.cell = prevGrowCell;
		}

		list.AddRange(dict.Values);
		list.Sort(delegate(CatalogEntry a, CatalogEntry b)
		{
			int c = a.Category.CompareTo(b.Category);
			return (c != 0) ? c : string.Compare(a.Label, b.Label, StringComparison.CurrentCulture);
		});
		return list;
	}

	private string GetBestTreeDisplayName(SourceObj.Row sObj, SourceMaterial.Row matObj)
	{
		string objName = GetCanonicalTreeObjName(sObj);
		string matName = GetCanonicalTreeMatName(matObj);
		if (sObj != null && sObj.tag != null && sObj.tag.Contains("mat_name") && !string.IsNullOrEmpty(matName))
		{
			return matName;
		}
		if (string.IsNullOrEmpty(objName) || objName == "树" || objName == "木" || string.Equals(objName, "tree", StringComparison.OrdinalIgnoreCase))
		{
			if (!string.IsNullOrEmpty(matName))
			{
				return matName;
			}
		}
		return (!string.IsNullOrEmpty(objName)) ? objName : matName;
	}

	/// <summary>从游戏原版数据源构建"同名/同类一键全包"分类词条库，去除一切重复编码项，无需进图即可勾选。</summary>
	public List<CatalogEntry> GetDatabaseCatalogByCategory(TargetItemCategory category)
	{
		UpgradeLegacyTargetedGatherKeysIfReady();
		List<CatalogEntry> list = new List<CatalogEntry>();
		Dictionary<string, CatalogEntry> dict = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);

		try
		{
			if (category == TargetItemCategory.Seed)
			{
				if (Cell.objList != null)
				{
					for (int i = 0; i < Cell.objList.Count; i++)
					{
						SourceObj.Row r = Cell.objList[i];
						if (r == null || r.id == 0 || (r.growth == null && !r.HasGrowth))
						{
							continue;
						}
						string seedName = GetCanonicalSeedName(r);
						if (string.IsNullOrEmpty(seedName))
						{
							continue;
						}
						string key = "seed:" + seedName;
						AddOrIncrementCatalogEntry(dict, key, "🌾 种子: " + seedName, TargetItemCategory.Seed, 0);
					}
				}
			}
			else if (category == TargetItemCategory.Tree)
			{
				HashSet<string> seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				if (Cell.objList != null)
				{
					for (int i = 0; i < Cell.objList.Count; i++)
					{
						SourceObj.Row r = Cell.objList[i];
						if (!IsTreeObjRow(r))
						{
							continue;
						}
						string treeName = GetCanonicalTreeObjName(r);
						if (string.IsNullOrEmpty(treeName) || treeName == "树" || treeName == "木" || string.Equals(treeName, "tree", StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						string root = GetTreeSpeciesRoot(treeName);
						if (!string.IsNullOrEmpty(root))
						{
							seenRoots.Add(root);
						}
						string key = "tree:" + treeName;
						AddOrIncrementCatalogEntry(dict, key, "🪓 树木: " + treeName, TargetItemCategory.Tree, 0);
					}
				}
				if (Cell.matList != null)
				{
					for (int i = 0; i < Cell.matList.Count; i++)
					{
						SourceMaterial.Row m = Cell.matList[i];
						if (m == null || m.id == 0 || m.category != "wood")
						{
							continue;
						}
						string mName = GetCanonicalTreeMatName(m);
						if (string.IsNullOrEmpty(mName))
						{
							continue;
						}
						string root = GetTreeSpeciesRoot(mName);
						if (!string.IsNullOrEmpty(root) && seenRoots.Contains(root))
						{
							continue;
						}
						if (!string.IsNullOrEmpty(root))
						{
							seenRoots.Add(root);
						}
						string key = "tree:" + mName;
						AddOrIncrementCatalogEntry(dict, key, "🪓 树木: " + mName, TargetItemCategory.Tree, 0);
					}
				}
			}
			else if (category == TargetItemCategory.Gather)
			{
				// 1. 所有采集类 Obj（包含蘑菇 GrowSystemKinoko、草药 GrowSystemHerb、花 GrowSystemFlower、浆果 GrowSystemBerry、杂草 GrowSystemWeed、蜘蛛网、藤蔓、残骸 mound 等）
				if (Cell.objList != null)
				{
					for (int i = 0; i < Cell.objList.Count; i++)
					{
						SourceObj.Row r = Cell.objList[i];
						if (!IsGatherObjRow(r))
						{
							continue;
						}
						string gName = GetCanonicalGatherObjName(r);
						if (string.IsNullOrEmpty(gName))
						{
							continue;
						}
						string key = "gather:" + gName;
						AddOrIncrementCatalogEntry(dict, key, "🕸️ 采集: " + gName, TargetItemCategory.Gather, 0);
					}
				}
				// 2. 所有可采集 Thing（如蜘蛛网 TraitCobWeb、TraitHarvest 等），与 Obj 共用同一套 gather:<名称> 自动去重合并
				if (EClass.sources?.things?.rows != null)
				{
					List<SourceThing.Row> tRows = EClass.sources.things.rows;
					for (int i = 0; i < tRows.Count; i++)
					{
						SourceThing.Row tr = tRows[i];
						if (tr == null || string.IsNullOrEmpty(tr.id))
						{
							continue;
						}
						if (IsHarvestableThingRow(tr, out var thingEle) && thingEle != 220 && thingEle != 230 && thingEle != 225)
						{
							string tName = GetCanonicalGatherThingName(tr);
							if (!string.IsNullOrEmpty(tName))
							{
								string key = "gather:" + tName;
								AddOrIncrementCatalogEntry(dict, key, "🕸️ 采集: " + tName, TargetItemCategory.Gather, 0);
							}
						}
					}
				}
			}
			else if (category == TargetItemCategory.Mine)
			{
				// 1. 所有矿石/宝石/水晶/岩石/土/骨材质（按材质一键全包：如勾选"铜"则通采所有铜矿脉/铜矿石/铜晶簇）
				if (Cell.matList != null)
				{
					for (int i = 0; i < Cell.matList.Count; i++)
					{
						SourceMaterial.Row m = Cell.matList[i];
						if (m == null || m.id == 0 || string.IsNullOrEmpty(m.alias))
						{
							continue;
						}
						string cat = m.category ?? "";
						if (cat != "ore" && cat != "gem" && cat != "crystal" && cat != "rock" && cat != "soil" && cat != "bone")
						{
							continue;
						}
						string mName = CleanCatalogItemName(SafeGetMatName(m));
						if (string.IsNullOrEmpty(mName))
						{
							continue;
						}
						string catTag = GetMineMaterialCategoryTag(cat);
						string key = "mine_mat:" + m.alias;
						AddOrIncrementCatalogEntry(dict, key, "⛏️ [" + catTag + "] " + mName, TargetItemCategory.Mine, 0);
					}
				}
				// 2. 所有采掘类 Obj 类型（同名自动合并：例如 id=18 与 id=19 两个"矿石"合并为唯一一个"矿石"条目）
				if (Cell.objList != null)
				{
					for (int i = 0; i < Cell.objList.Count; i++)
					{
						SourceObj.Row r = Cell.objList[i];
						if (!IsMineObjRow(r))
						{
							continue;
						}
						string name = GetCanonicalMineObjName(r);
						if (string.IsNullOrEmpty(name))
						{
							continue;
						}
						string key = "mine_obj:" + name;
						AddOrIncrementCatalogEntry(dict, key, "🪨 [采掘物类型] " + name, TargetItemCategory.Mine, 0);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log("GetDatabaseCatalogByCategory error: " + ex.Message);
		}

		list.AddRange(dict.Values);
		list.Sort((CatalogEntry a, CatalogEntry b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCulture));

		// 在每个分类顶部插入"全选该大类"的一键总包项
		switch (category)
		{
		case TargetItemCategory.Seed:
			list.Insert(0, new CatalogEntry
			{
				Key = "seed:*",
				Label = "🌾 [全部种子] 所有成熟可收种植物",
				Category = TargetItemCategory.Seed,
				MapCount = 0
			});
			break;
		case TargetItemCategory.Tree:
			list.Insert(0, new CatalogEntry
			{
				Key = "tree:*",
				Label = "🪓 [全部树木] 不限树种全部砍伐",
				Category = TargetItemCategory.Tree,
				MapCount = 0
			});
			break;
		case TargetItemCategory.Gather:
			list.Insert(0, new CatalogEntry
			{
				Key = "gather:*",
				Label = "🕸️ [全部采集物] 所有蘑菇/蜘蛛网/花草/杂物",
				Category = TargetItemCategory.Gather,
				MapCount = 0
			});
			break;
		case TargetItemCategory.Mine:
			list.Insert(0, new CatalogEntry
			{
				Key = "mine:*",
				Label = "⛏️ [全部矿石/采掘物] 所有矿石/晶簇/宝石/岩石",
				Category = TargetItemCategory.Mine,
				MapCount = 0
			});
			break;
		}

		return list;
	}

	/// <summary>严格依据 CardRow.cs 中 public string[] trait 与 TraitCobWeb.cs / TraitHarvest.cs 判定可采集 Thing 行及其对应元素 ID。</summary>
	private bool IsHarvestableThingRow(SourceThing.Row tr, out int reqEle)
	{
		reqEle = -1;
		if (tr == null || string.IsNullOrEmpty(tr.id))
		{
			return false;
		}
		if (tr.trait != null && tr.trait.Length > 0 && !string.IsNullOrEmpty(tr.trait[0]))
		{
			string t0 = tr.trait[0];
			if (t0 == "CobWeb")
			{
				reqEle = 250;
				return true;
			}
			if (t0 == "Harvest")
			{
				string rawSkill = (tr.trait.Length > 1 && !string.IsNullOrEmpty(tr.trait[1])) ? tr.trait[1] : "gathering";
				int ele = ResolveHarvestElementByRawKey(rawSkill);
				reqEle = (ele > 0) ? ele : 250;
				return true;
			}
		}
		if (IsCobwebThingRow(tr))
		{
			reqEle = 250;
			return true;
		}
		return false;
	}

	private void AddOrIncrementCatalogEntry(Dictionary<string, CatalogEntry> dict, string key, string label, TargetItemCategory cat, int addCount = 1)
	{
		if (dict.TryGetValue(key, out var existing))
		{
			existing.MapCount += addCount;
			dict[key] = existing;
		}
		else
		{
			dict[key] = new CatalogEntry
			{
				Key = key,
				Label = label,
				Category = cat,
				MapCount = addCount
			};
		}
	}

	private string SafeGetObjName(SourceObj.Row r)
	{
		if (r == null)
		{
			return "";
		}
		try
		{
			string n = r.GetName();
			if (!string.IsNullOrEmpty(n))
			{
				return n;
			}
		}
		catch
		{
		}
		return (!string.IsNullOrEmpty(r.name)) ? r.name : ((!string.IsNullOrEmpty(r.name_JP)) ? r.name_JP : (r.alias ?? ""));
	}

	private string SafeGetMatName(SourceMaterial.Row m)
	{
		if (m == null)
		{
			return "";
		}
		try
		{
			string n = m.GetName();
			if (!string.IsNullOrEmpty(n))
			{
				return n;
			}
		}
		catch
		{
		}
		return (!string.IsNullOrEmpty(m.name)) ? m.name : ((!string.IsNullOrEmpty(m.name_JP)) ? m.name_JP : (m.alias ?? ""));
	}

	private string SafeGetThingName(Thing t)
	{
		if (t == null)
		{
			return "";
		}
		try
		{
			string n = t.source?.GetName();
			if (!string.IsNullOrEmpty(n))
			{
				return n;
			}
			return t.Name;
		}
		catch
		{
			return t.id ?? "";
		}
	}

	private string SafeGetThingRowName(SourceThing.Row tr)
	{
		if (tr == null)
		{
			return "";
		}
		try
		{
			string n = tr.GetName();
			if (!string.IsNullOrEmpty(n))
			{
				return n;
			}
		}
		catch
		{
		}
		return (!string.IsNullOrEmpty(tr.name)) ? tr.name : ((!string.IsNullOrEmpty(tr.name_JP)) ? tr.name_JP : (tr.id ?? ""));
	}

	// ---------- 🎯 专项收集跨图通用匹配判定 ----------
	private bool IsSeedTargetMatched(SourceObj.Row sObj)
	{
		if (sObj == null || sObj.id == 0 || TargetedGatherKeys.Count == 0)
		{
			return false;
		}
		if (TargetedGatherKeys.Contains("seed:*"))
		{
			return true;
		}
		string seedName = GetCanonicalSeedName(sObj);
		if (!string.IsNullOrEmpty(seedName) && TargetedGatherKeys.Contains("seed:" + seedName))
		{
			return true;
		}
		string rawObjName = CleanCatalogItemName(SafeGetObjName(sObj));
		if (!string.IsNullOrEmpty(rawObjName) && TargetedGatherKeys.Contains("seed:" + rawObjName))
		{
			return true;
		}
		if (!string.IsNullOrEmpty(sObj.alias) && TargetedGatherKeys.Contains("seed:" + sObj.alias))
		{
			return true;
		}
		return false;
	}

	private bool IsTreeTargetMatched(SourceObj.Row sObj, SourceMaterial.Row matObj)
	{
		if ( sObj == null || sObj.id == 0 || TargetedGatherKeys.Count == 0)
		{
			return false;
		}
		if (TargetedGatherKeys.Contains("tree:*"))
		{
			return true;
		}
		string objTreeName = GetCanonicalTreeObjName(sObj);
		if (!string.IsNullOrEmpty(objTreeName) && (TargetedGatherKeys.Contains("tree:" + objTreeName) || TargetedGatherKeys.Contains("gather:" + objTreeName)))
		{
			return true;
		}
		string matTreeName = GetCanonicalTreeMatName(matObj);
		if (!string.IsNullOrEmpty(matTreeName) && TargetedGatherKeys.Contains("tree:" + matTreeName))
		{
			return true;
		}
		// 树种词根互通匹配（例如"桦木"与"白桦"、"橡木"与"橡树"、"雪松木"与"雪松"）
		string objRoot = GetTreeSpeciesRoot(objTreeName);
		string matRoot = GetTreeSpeciesRoot(matTreeName);
		foreach (string k in TargetedGatherKeys)
		{
			if (!k.StartsWith("tree:", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string targetTree = k.Substring(5).Trim();
			string targetRoot = GetTreeSpeciesRoot(targetTree);
			if (!string.IsNullOrEmpty(targetRoot))
			{
				if ((!string.IsNullOrEmpty(objRoot) && string.Equals(objRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
					|| (!string.IsNullOrEmpty(matRoot) && string.Equals(matRoot, targetRoot, StringComparison.OrdinalIgnoreCase)))
				{
					return true;
				}
			}
		}
		// 兼容旧版编码格式
		string objAlias = sObj.alias ?? "";
		string matAlias = matObj?.alias ?? "";
		if (!string.IsNullOrEmpty(objAlias) && TargetedGatherKeys.Contains("tree_obj:" + objAlias))
		{
			return true;
		}
		if (!string.IsNullOrEmpty(matAlias) && (TargetedGatherKeys.Contains("tree_mat:" + matAlias) || (!string.IsNullOrEmpty(objAlias) && TargetedGatherKeys.Contains("objmat:" + objAlias + ":" + matAlias))))
		{
			return true;
		}
		return false;
	}

	private bool IsGatherObjTargetMatched(SourceObj.Row sObj, SourceMaterial.Row matObj)
	{
		if (sObj == null || sObj.id == 0 || TargetedGatherKeys.Count == 0)
		{
			return false;
		}
		if (TargetedGatherKeys.Contains("gather:*"))
		{
			return IsGatherObjRow(sObj);
		}
		string canonicalName = GetCanonicalGatherObjName(sObj);
		if (!string.IsNullOrEmpty(canonicalName) && (TargetedGatherKeys.Contains("gather:" + canonicalName) || TargetedGatherKeys.Contains("tree:" + canonicalName)))
		{
			return true;
		}
		string rawName = CleanCatalogItemName(SafeGetObjName(sObj));
		if (!string.IsNullOrEmpty(rawName) && (TargetedGatherKeys.Contains("gather:" + rawName) || TargetedGatherKeys.Contains("tree:" + rawName)))
		{
			return true;
		}
		string objAlias = sObj.alias ?? "";
		string matAlias = matObj?.alias ?? "";
		if (!string.IsNullOrEmpty(objAlias) && (TargetedGatherKeys.Contains("gather_obj:" + objAlias) || TargetedGatherKeys.Contains("gather_thing:" + objAlias) || TargetedGatherKeys.Contains("tree_obj:" + objAlias)))
		{
			return true;
		}
		if (!string.IsNullOrEmpty(objAlias) && !string.IsNullOrEmpty(matAlias) && TargetedGatherKeys.Contains("objmat:" + objAlias + ":" + matAlias))
		{
			return true;
		}
		return false;
	}

	private bool IsGatherThingTargetMatched(Thing t)
	{
		if (t == null || TargetedGatherKeys.Count == 0)
		{
			return false;
		}
		if (TargetedGatherKeys.Contains("gather:*"))
		{
			return true;
		}
		string canonicalName = GetCanonicalGatherThingName(t.source, t.trait);
		if (!string.IsNullOrEmpty(canonicalName) && TargetedGatherKeys.Contains("gather:" + canonicalName))
		{
			return true;
		}
		string rawName = CleanCatalogItemName(SafeGetThingName(t));
		if (!string.IsNullOrEmpty(rawName) && TargetedGatherKeys.Contains("gather:" + rawName))
		{
			return true;
		}
		string tId = ((Card)t).id ?? "";
		if (!string.IsNullOrEmpty(tId) && (TargetedGatherKeys.Contains("gather_thing:" + tId) || TargetedGatherKeys.Contains("gather_obj:" + tId) || TargetedGatherKeys.Contains("gather:" + tId)))
		{
			return true;
		}
		return false;
	}

	private bool IsMineMaterialMatched(SourceMaterial.Row mat)
	{
		if (mat == null || mat.id == 0 || TargetedGatherKeys.Count == 0)
		{
			return false;
		}
		if (TargetedGatherKeys.Contains("mine:*"))
		{
			return true;
		}
		if (!string.IsNullOrEmpty(mat.alias) && TargetedGatherKeys.Contains("mine_mat:" + mat.alias))
		{
			return true;
		}
		string matName = CleanCatalogItemName(SafeGetMatName(mat));
		if (!string.IsNullOrEmpty(matName) && TargetedGatherKeys.Contains("mine_mat:" + matName))
		{
			return true;
		}
		return false;
	}

	private bool IsMineObjTargetMatched(SourceObj.Row sObj, SourceMaterial.Row matObj)
	{
		if (sObj == null || sObj.id == 0 || TargetedGatherKeys.Count == 0)
		{
			return false;
		}
		if (TargetedGatherKeys.Contains("mine:*"))
		{
			return true;
		}
		if (IsMineMaterialMatched(matObj))
		{
			return true;
		}
		string mineObjName = GetCanonicalMineObjName(sObj);
		if (!string.IsNullOrEmpty(mineObjName) && (TargetedGatherKeys.Contains("mine_obj:" + mineObjName) || TargetedGatherKeys.Contains("gather:" + mineObjName)))
		{
			return true;
		}
		string objAlias = sObj.alias ?? "";
		string matAlias = matObj?.alias ?? "";
		if (!string.IsNullOrEmpty(objAlias) && TargetedGatherKeys.Contains("mine_obj:" + objAlias))
		{
			return true;
		}
		if (!string.IsNullOrEmpty(objAlias) && !string.IsNullOrEmpty(matAlias) && TargetedGatherKeys.Contains("objmat:" + objAlias + ":" + matAlias))
		{
			return true;
		}
		return false;
	}

	private bool IsMineThingTargetMatched(Thing t)
	{
		if (t == null || TargetedGatherKeys.Count == 0)
		{
			return false;
		}
		if (TargetedGatherKeys.Contains("mine:*"))
		{
			return true;
		}
		if (IsMineMaterialMatched(t.material))
		{
			return true;
		}
		string tName = CleanCatalogItemName(SafeGetThingName(t));
		if (!string.IsNullOrEmpty(tName) && (TargetedGatherKeys.Contains("mine_obj:" + tName) || TargetedGatherKeys.Contains("gather:" + tName)))
		{
			return true;
		}
		string tId = ((Card)t).id ?? "";
		if (!string.IsNullOrEmpty(tId) && TargetedGatherKeys.Contains("gather_thing:" + tId))
		{
			return true;
		}
		return false;
	}

	public int GetCurrentBurdenPercent(Chara pc = null)
	{
		if (pc == null)
		{
			pc = EClass.pc;
		}
		if (pc == null)
		{
			return 0;
		}
		long limit = Math.Max(1L, (long)((Card)pc).WeightLimit);
		long pct = (long)((Card)pc).ChildrenWeight * 100L / limit;
		return (pct < 0L) ? 1000 : (int)Math.Min(pct, 9999L);
	}

	/// <summary>读取角色当前剩余氧气百分比（100% = 无窒息满氧气；50% = ConSuffocation.value 为 50；0% = ConSuffocation.value >= 100 开始扣血溺水）。</summary>
	public int GetCurrentOxygenPercent(Chara pc = null)
	{
		if (pc == null)
		{
			pc = EClass.pc;
		}
		if (pc == null)
		{
			return 100;
		}
		ConSuffocation suff = pc.GetCondition<ConSuffocation>();
		if (suff == null)
		{
			return 100;
		}
		int suffVal = suff.value;
		return Mathf.Clamp(100 - suffVal, 0, 100);
	}

	/// <summary>直接从原版 EClass._map.bounds 获取当前地图真实的玩家可行动边界（Cell.Refresh 中判定 outOfBounds 的唯一依据，家园扩建后会自动扩大）。</summary>
	public bool TryGetMapPlayableBounds(out int bMinX, out int bMaxX, out int bMinZ, out int bMaxZ)
	{
		bMinX = 0;
		bMaxX = -1;
		bMinZ = 0;
		bMaxZ = -1;
		if (EClass._map == null)
		{
			return false;
		}
		int size = ((MapBounds)EClass._map).Size;
		if (size <= 0)
		{
			return false;
		}
		MapBounds bounds = EClass._map.bounds;
		if (bounds != null && bounds.maxX >= bounds.x && bounds.maxZ >= bounds.z && bounds.maxX > 0 && bounds.maxZ > 0)
		{
			bMinX = Mathf.Clamp(bounds.x, 0, size - 1);
			bMaxX = Mathf.Clamp(bounds.maxX, 0, size - 1);
			bMinZ = Mathf.Clamp(bounds.z, 0, size - 1);
			bMaxZ = Mathf.Clamp(bounds.maxZ, 0, size - 1);
			return true;
		}
		bMinX = 0;
		bMaxX = size - 1;
		bMinZ = 0;
		bMaxZ = size - 1;
		return true;
	}

	/// <summary>严格对齐原版 Chara.CanInteractTo(Point p) 与 Los.IsVisible(p1, p2) 的相邻格无隔墙/无斜角墙阻挡判定。
	/// 解决原版 PathProgress / AI_Goto 在 destDist=1 时把"斜角隔着墙角的格子"或"隔着薄墙的格子"误判为已到达的问题。</summary>
	private bool CanInteractBetweenPoints(Point fromPos, Point targetPos, bool allowBlockedTarget = false)
	{
		if (fromPos == null || targetPos == null || !fromPos.IsValid || !targetPos.IsValid || EClass._map == null)
		{
			return false;
		}
		if (fromPos.Equals(targetPos))
		{
			return true;
		}
		int dist = fromPos.Distance(targetPos);
		if (dist > 1)
		{
			return false;
		}
		Cell[,] cells = EClass._map.cells;
		if (cells == null)
		{
			return false;
		}
		Cell fromCell = cells[fromPos.x, fromPos.z];
		Cell targetCell = cells[targetPos.x, targetPos.z];
		if (fromCell == null || targetCell == null || fromCell.outOfBounds || targetCell.outOfBounds)
		{
			return false;
		}
		if (allowBlockedTarget && targetCell.blocked)
		{
			// 针对实心方块/墙面目标：若正交相邻则直接可交互；若斜角相邻则要求至少一侧拐角未被墙体或越界封死
			if (fromPos.x != targetPos.x && fromPos.z != targetPos.z)
			{
				Cell c1 = cells[targetPos.x, fromPos.z];
				Cell c2 = cells[fromPos.x, targetPos.z];
				bool corner1Blocked = c1 == null || c1.outOfBounds || c1.blocked;
				bool corner2Blocked = c2 == null || c2.outOfBounds || c2.blocked;
				if (corner1Blocked && corner2Blocked)
				{
					return false;
				}
			}
			return true;
		}

		// 对齐 Chara.CanInteractTo(Point p) 第 2552-2584 行：检查正交与斜角四个方向的通行权重 weights
		int dir = ((targetPos.z >= fromPos.z) ? ((targetPos.x > fromPos.x) ? 1 : ((targetPos.z > fromPos.z) ? 2 : 3)) : 0);
		if (fromCell.weights == null || fromCell.weights[dir] == 0)
		{
			return false;
		}
		if (targetPos.x != fromPos.x && targetPos.z != fromPos.z)
		{
			int x = targetPos.x;
			int z = fromPos.z;
			if (cells[x, z] == null || cells[x, z].outOfBounds || cells[x, z].blocked)
			{
				return false;
			}
			int dir2 = ((z >= fromPos.z) ? ((x > fromPos.x) ? 1 : ((z > fromPos.z) ? 2 : 3)) : 0);
			if (fromCell.weights[dir2] == 0)
			{
				return false;
			}
			dir2 = ((z >= targetPos.z) ? ((x > targetPos.x) ? 1 : ((z > targetPos.z) ? 2 : 3)) : 0);
			if (targetCell.weights == null || targetCell.weights[dir2] == 0)
			{
				return false;
			}
			x = fromPos.x;
			z = targetPos.z;
			if (cells[x, z] == null || cells[x, z].outOfBounds || cells[x, z].blocked)
			{
				return false;
			}
			dir2 = ((z >= fromPos.z) ? ((x > fromPos.x) ? 1 : ((z > fromPos.z) ? 2 : 3)) : 0);
			if (fromCell.weights[dir2] == 0)
			{
				return false;
			}
			dir2 = ((z >= targetPos.z) ? ((x > targetPos.x) ? 1 : ((z > targetPos.z) ? 2 : 3)) : 0);
			if (targetCell.weights[dir2] == 0)
			{
				return false;
			}
		}
		return Los.IsVisible(fromPos, targetPos);
	}

	/// <summary>判断玩家在当前位置是否已经可以直接无阻挡攻击目标敌人（相邻、无隔墙/斜角墙阻挡且有真实视线 Los.IsVisible）。</summary>
	private bool CanDirectlyAttackEnemy(Chara pc, Chara enemy)
	{
		if (pc == null || enemy == null || enemy.isDead)
		{
			return false;
		}
		Point pcPos = ((Card)pc).pos;
		Point ePos = ((Card)enemy).pos;
		if (pcPos == null || ePos == null || !pcPos.IsValid || !ePos.IsValid)
		{
			return false;
		}
		if (pcPos.Distance(ePos) > 1)
		{
			return false;
		}
		if (!pc.CanInteractTo(ePos))
		{
			return false;
		}
		if (!CanInteractBetweenPoints(pcPos, ePos, allowBlockedTarget: false))
		{
			return false;
		}
		return Los.IsVisible(pcPos, ePos);
	}

	/// <summary>严格对齐 Chara.CanMoveTo (2498-2526行) 与 Chara._Move (2989-3000行) / Chara.CanReplace (3279行)：
	/// 玩家走上单个同伴/可推动非敌对角色所在格时会自动与之交换位置 (MoveByForce)，不视为阻挡；
	/// 仅当该格存在敌对生物、不可推动角色、或多角色叠站无法单人置换时才视为角色阻挡。</summary>
	private bool IsTileBlockedByNonReplaceableChara(Chara pc, Point p)
	{
		if (pc == null || p == null || !p.IsValid || !p.HasChara)
		{
			return false;
		}
		Point pcPos = ((Card)pc).pos;
		if (pcPos != null && p.Equals(pcPos))
		{
			return false;
		}
		if (pc.IsEnemyOnPath(p, cancelAI: false))
		{
			return true;
		}
		List<Chara> charas = p.Charas;
		if (charas != null && charas.Count == 1)
		{
			Chara c = charas[0];
			if (c != null && pc.CanReplace(c))
			{
				return false;
			}
		}
		return true;
	}

	// 对齐 PathFinder.cs 第 108-118 行 mDirection 顺序：先正交 4 方向 {0,-1},{1,0},{0,1},{-1,0}，后对角 4 方向 {-1,-1},{1,-1},{-1,1},{1,1}
	private static readonly int[] _pfDirX = new int[8] { 0, 1, 0, -1, -1, 1, -1, 1 };
	private static readonly int[] _pfDirZ = new int[8] { -1, 0, 1, 0, -1, -1, 1, 1 };

	/// <summary>确保安全 A* 寻路缓存已按当前地图尺寸分配。</summary>
	private void EnsureSafePathBuffers(int mapSize)
	{
		int total = mapSize * mapSize;
		if (_pfVisitId == null || _pfVisitId.Length < total || _pfMapSize != mapSize)
		{
			_pfMapSize = mapSize;
			_pfRunId = 1;
			_pfVisitId = new int[total];
			_pfClosedId = new int[total];
			_pfGScore = new int[total];
			_pfParentXZ = new int[total];
			_pfHeapXZ = new int[total + 1];
			_pfHeapF = new int[total + 1];
		}
		_pfRunId++;
		if (_pfRunId >= int.MaxValue - 2)
		{
			Array.Clear(_pfVisitId, 0, _pfVisitId.Length);
			Array.Clear(_pfClosedId, 0, _pfClosedId.Length);
			_pfRunId = 1;
		}
	}

	/// <summary>强制使单源寻路距离场缓存失效（当挖穿墙体、切换地图、修改避障或深水开关时调用）。</summary>
	private void InvalidateDistanceFieldCache()
	{
		_dfCacheTime = -1f;
		_dfCachePcX = int.MinValue;
		_dfCachePcZ = int.MinValue;
	}

	/// <summary>严格对齐原版 PathFinder._FindPath（第 220-262 行）与 Chara.CanMoveTo（第 2447-2496 行）的相邻格移动校验：
	/// 使用原版 Cell.weights[0..3]（由 Cell.Refresh 根据高度差 <= 8 与通行性实时计算）及斜角两侧拐角格校验，
	/// 彻底杜绝斜角卡墙或高差不可达导致的死循环。</summary>
	private bool CanStepBetweenAdjacentCells(Cell[,] cells, int cx, int cz, int nx, int nz)
	{
		Cell c1 = cells[cx, cz];
		Cell c2 = cells[nx, nz];
		if (c1 == null || c2 == null || c2.outOfBounds || c2.blocked || c2.isSurrounded)
		{
			return false;
		}
		int dir = ((nz >= cz) ? ((nx > cx) ? 1 : ((nz > cz) ? 2 : 3)) : 0);
		if (c1.weights == null || c1.weights[dir] == 0)
		{
			return false;
		}
		// 正交移动
		if (nx == cx || nz == cz)
		{
			return true;
		}
		// 对角线移动：严格对齐 Chara.CanMoveTo 第 2461-2496 行与 PathFinder.cs 第 227-261 行
		int mx = nx;
		int mz = cz;
		Cell cHoriz = cells[mx, mz];
		if (cHoriz == null || cHoriz.outOfBounds || cHoriz.blocked)
		{
			return false;
		}
		int dir2 = ((mz >= cz) ? ((mx > cx) ? 1 : ((mz > cz) ? 2 : 3)) : 0);
		if (c1.weights[dir2] == 0)
		{
			return false;
		}
		dir2 = ((mz >= nz) ? ((mx > nx) ? 1 : ((mz > nz) ? 2 : 3)) : 0);
		if (c2.weights == null || c2.weights[dir2] == 0)
		{
			return false;
		}

		mx = cx;
		mz = nz;
		Cell cVert = cells[mx, mz];
		if (cVert == null || cVert.outOfBounds || cVert.blocked)
		{
			return false;
		}
		dir2 = ((mz >= cz) ? ((mx > cx) ? 1 : ((mz > cz) ? 2 : 3)) : 0);
		if (c1.weights[dir2] == 0)
		{
			return false;
		}
		dir2 = ((mz >= nz) ? ((mx > nx) ? 1 : ((mz > nz) ? 2 : 3)) : 0);
		if (c2.weights[dir2] == 0)
		{
			return false;
		}
		return true;
	}

	/// <summary>构建从玩家当前坐标出发的全图单源安全寻路距离场（Dijkstra）：
	/// 一次扫描即可获知地图上每一个可落脚格子的【真实绕墙行走步数/代价】，
	/// 使寻怪、捡物、采集、脱困全部按“路途上的近”而非“穿墙直线近”决策。</summary>
	private void EnsureSafeDistanceField(Chara pc)
	{
		if (pc == null || EClass._map == null)
		{
			return;
		}
		Point pcPos = ((Card)pc).pos;
		if (pcPos == null || !pcPos.IsValid)
		{
			return;
		}
		int size = EClass._map.Size;
		if (size <= 0 || pcPos.x < 0 || pcPos.z < 0 || pcPos.x >= size || pcPos.z >= size)
		{
			return;
		}

		float now = Time.time;
		if (_dfCacheMap == EClass._map
			&& _dfMapSize == size
			&& _dfCachePcX == pcPos.x
			&& _dfCachePcZ == pcPos.z
			&& _dfCacheAvoidTraps == EffectiveAvoidTraps
			&& _dfCacheIgnoreDeepWater == IgnoreDeepWater
			&& _dfCacheSwimMode == SwimModeEnabled
			&& _dfCacheSwimRecovering == _isSwimRecoveringOxygen
			&& now >= _dfCacheTime
			&& now - _dfCacheTime < 0.25f)
		{
			return;
		}

		_dfCacheMap = EClass._map;
		_dfCachePcX = pcPos.x;
		_dfCachePcZ = pcPos.z;
		_dfCacheAvoidTraps = EffectiveAvoidTraps;
		_dfCacheIgnoreDeepWater = IgnoreDeepWater;
		_dfCacheSwimMode = SwimModeEnabled;
		_dfCacheSwimRecovering = _isSwimRecoveringOxygen;
		_dfCacheTime = now;

		int total = size * size;
		if (_dfStrictVisitId == null || _dfStrictVisitId.Length < total || _dfMapSize != size)
		{
			_dfMapSize = size;
			_dfStrictRunId = 1;
			_dfPassHostileRunId = 1;
			_dfStrictVisitId = new int[total];
			_dfStrictG = new int[total];
			_dfPassHostileVisitId = new int[total];
			_dfPassHostileG = new int[total];
		}

		RunSingleSourceDijkstra(pc, pcPos.x, pcPos.z, size, allowPassThroughHostile: false, ref _dfStrictRunId, _dfStrictVisitId, _dfStrictG);
		RunSingleSourceDijkstra(pc, pcPos.x, pcPos.z, size, allowPassThroughHostile: true, ref _dfPassHostileRunId, _dfPassHostileVisitId, _dfPassHostileG);
	}

	private void RunSingleSourceDijkstra(Chara pc, int startX, int startZ, int size, bool allowPassThroughHostile, ref int distRunId, int[] visitArr, int[] gArr)
	{
		EnsureSafePathBuffers(size);
		int closedId = _pfRunId;
		distRunId++;
		if (distRunId >= int.MaxValue - 2)
		{
			Array.Clear(visitArr, 0, visitArr.Length);
			distRunId = 1;
		}
		int dRun = distRunId;
		int startXZ = startX + startZ * size;
		visitArr[startXZ] = dRun;
		gArr[startXZ] = 0;

		int heapSize = 0;
		void HeapPush(int xz, int gVal)
		{
			heapSize++;
			int idx = heapSize;
			while (idx > 1)
			{
				int parent = idx >> 1;
				if (_pfHeapF[parent] <= gVal)
				{
					break;
				}
				_pfHeapXZ[idx] = _pfHeapXZ[parent];
				_pfHeapF[idx] = _pfHeapF[parent];
				idx = parent;
			}
			_pfHeapXZ[idx] = xz;
			_pfHeapF[idx] = gVal;
		}

		int HeapPop()
		{
			int ret = _pfHeapXZ[1];
			int lastXZ = _pfHeapXZ[heapSize];
			int lastF = _pfHeapF[heapSize];
			heapSize--;
			int idx = 1;
			int half = heapSize >> 1;
			while (idx <= half)
			{
				int left = idx << 1;
				int right = left + 1;
				int smallest = left;
				if (right <= heapSize && _pfHeapF[right] < _pfHeapF[left])
				{
					smallest = right;
				}
				if (lastF <= _pfHeapF[smallest])
				{
					break;
				}
				_pfHeapXZ[idx] = _pfHeapXZ[smallest];
				_pfHeapF[idx] = _pfHeapF[smallest];
				idx = smallest;
			}
			if (heapSize > 0)
			{
				_pfHeapXZ[idx] = lastXZ;
				_pfHeapF[idx] = lastF;
			}
			return ret;
		}

		HeapPush(startXZ, 0);
		Cell[,] cells = EClass._map.cells;
		int maxSteps = size * size;
		int steps = 0;

		while (heapSize > 0 && steps < maxSteps)
		{
			int curXZ = HeapPop();
			if (_pfClosedId[curXZ] == closedId)
			{
				continue;
			}
			_pfClosedId[curXZ] = closedId;
			steps++;

			int cx = curXZ % size;
			int cz = curXZ / size;
			int curG = gArr[curXZ];

			// 严格对齐原版 PathFinder.cs (第 108-118 行) mDirection 顺序：先遍历 4 个正交方向，再遍历 4 个对角方向
			for (int dirIdx = 0; dirIdx < 8; dirIdx++)
			{
				{
					int dx = _pfDirX[dirIdx];
					int dz = _pfDirZ[dirIdx];
					int nx = cx + dx;
					int nz = cz + dz;
					if (nx < 0 || nz < 0 || nx >= size || nz >= size)
					{
						continue;
					}
					int nextXZ = nx + nz * size;
					if (_pfClosedId[nextXZ] == closedId)
					{
						continue;
					}
					if (!CanStepBetweenAdjacentCells(cells, cx, cz, nx, nz))
					{
						continue;
					}
					Cell nc = cells[nx, nz];
					if (IsForbiddenDeepWater(nc, pc))
					{
						continue;
					}
					if (EffectiveAvoidTraps && HasTrapOnCell(nc, pc))
					{
						continue;
					}

					int stepCost = ((dx != 0 && dz != 0) ? 14 : 10);
					if (nc.hasDoor)
					{
						stepCost += 4;
					}
					if (nc.detail != null && nc.detail.charas.Count > 0)
					{
						Point np = new Point(nx, nz);
						if (IsTileBlockedByNonReplaceableChara(pc, np))
						{
							if (!allowPassThroughHostile)
							{
								continue;
							}
							stepCost += 120;
						}
						else
						{
							stepCost += 15;
						}
					}

					int tentativeG = curG + stepCost;
					if (visitArr[nextXZ] != dRun || tentativeG < gArr[nextXZ])
					{
						visitArr[nextXZ] = dRun;
						gArr[nextXZ] = tentativeG;
						HeapPush(nextXZ, tentativeG);
					}
				}
			}
		}
	}

	/// <summary>查询玩家到达指定可落脚格子 (x, z) 的真实寻路距离（单位：格，正交 1.0 格，斜角 1.4 格）。
	/// 若不可达则返回 false。</summary>
	private bool TryGetStandTilePathDistance(Chara pc, int x, int z, bool allowPassThroughHostile, out float pathDist)
	{
		pathDist = float.MaxValue;
		if (pc == null || EClass._map == null)
		{
			return false;
		}
		int size = EClass._map.Size;
		if (x < 0 || z < 0 || x >= size || z >= size)
		{
			return false;
		}
		Point pcPos = ((Card)pc).pos;
		if (pcPos != null && pcPos.x == x && pcPos.z == z)
		{
			pathDist = 0f;
			return true;
		}
		EnsureSafeDistanceField(pc);
		int idx = x + z * size;
		if (!allowPassThroughHostile)
		{
			if (_dfStrictVisitId != null && idx < _dfStrictVisitId.Length && _dfStrictVisitId[idx] == _dfStrictRunId)
			{
				pathDist = (float)_dfStrictG[idx] * 0.1f;
				return true;
			}
			return false;
		}
		if (_dfStrictVisitId != null && idx < _dfStrictVisitId.Length && _dfStrictVisitId[idx] == _dfStrictRunId)
		{
			pathDist = (float)_dfStrictG[idx] * 0.1f;
			return true;
		}
		if (_dfPassHostileVisitId != null && idx < _dfPassHostileVisitId.Length && _dfPassHostileVisitId[idx] == _dfPassHostileRunId)
		{
			pathDist = (float)_dfPassHostileG[idx] * 0.1f;
			return true;
		}
		return false;
	}

	/// <summary>安全 A* 寻路：在途中与终点严格避开所有陷阱（含未发现的隐形陷阱 isHidden）及禁入深水区，
	/// 并支持对角线安全绕过陷阱格。若 buildNodes 为 true，则按原版 PathProgress.nodes 逆序（终点在 index 0）填充节点。</summary>
	private bool TryFindSafePath(Chara pc, Point start, Point dest, bool combat, bool allowPassThroughHostile, bool buildNodes, List<PathFinderNode> outNodes)
	{
		if (pc == null || start == null || dest == null || !start.IsValid || !dest.IsValid || EClass._map == null)
		{
			return false;
		}
		int size = EClass._map.Size;
		if (start.x < 0 || start.z < 0 || start.x >= size || start.z >= size || dest.x < 0 || dest.z < 0 || dest.x >= size || dest.z >= size)
		{
			return false;
		}
		if (start.Equals(dest))
		{
			if (buildNodes && outNodes != null)
			{
				outNodes.Clear();
				outNodes.Add(new PathFinderNode { X = dest.x, Z = dest.z });
			}
			return true;
		}

		Cell[,] cells = EClass._map.cells;
		Cell destCell = cells[dest.x, dest.z];
		if (destCell == null || destCell.outOfBounds || destCell.blocked || destCell.isSurrounded)
		{
			return false;
		}
		if (IsForbiddenDeepWater(destCell, pc))
		{
			return false;
		}
		if (EffectiveAvoidTraps && HasTrapOnCell(destCell, pc))
		{
			return false;
		}
		if (!allowPassThroughHostile && IsTileBlockedByNonReplaceableChara(pc, dest))
		{
			return false;
		}

		EnsureSafePathBuffers(size);
		int runId = _pfRunId;
		int startXZ = start.x + start.z * size;
		int destXZ = dest.x + dest.z * size;

		_pfVisitId[startXZ] = runId;
		_pfGScore[startXZ] = 0;
		_pfParentXZ[startXZ] = startXZ;

		int heapSize = 0;
		void HeapPush(int xz, int fVal)
		{
			heapSize++;
			int idx = heapSize;
			while (idx > 1)
			{
				int parent = idx >> 1;
				if (_pfHeapF[parent] <= fVal)
				{
					break;
				}
				_pfHeapXZ[idx] = _pfHeapXZ[parent];
				_pfHeapF[idx] = _pfHeapF[parent];
				idx = parent;
			}
			_pfHeapXZ[idx] = xz;
			_pfHeapF[idx] = fVal;
		}

		int HeapPop()
		{
			int ret = _pfHeapXZ[1];
			int lastXZ = _pfHeapXZ[heapSize];
			int lastF = _pfHeapF[heapSize];
			heapSize--;
			int idx = 1;
			int half = heapSize >> 1;
			while (idx <= half)
			{
				int left = idx << 1;
				int right = left + 1;
				int smallest = left;
				if (right <= heapSize && _pfHeapF[right] < _pfHeapF[left])
				{
					smallest = right;
				}
				if (lastF <= _pfHeapF[smallest])
				{
					break;
				}
				_pfHeapXZ[idx] = _pfHeapXZ[smallest];
				_pfHeapF[idx] = _pfHeapF[smallest];
				idx = smallest;
			}
			if (heapSize > 0)
			{
				_pfHeapXZ[idx] = lastXZ;
				_pfHeapF[idx] = lastF;
			}
			return ret;
		}

		int initH = (Mathf.Abs(start.x - dest.x) + Mathf.Abs(start.z - dest.z)) * 10;
		HeapPush(startXZ, initH);

		int steps = 0;
		int maxSteps = size * size;
		while (heapSize > 0 && steps < maxSteps)
		{
			int curXZ = HeapPop();
			if (_pfClosedId[curXZ] == runId)
			{
				continue;
			}
			_pfClosedId[curXZ] = runId;
			if (curXZ == destXZ)
			{
				if (buildNodes && outNodes != null)
				{
					outNodes.Clear();
					int trace = destXZ;
					int guard = 0;
					while (trace != startXZ && guard < maxSteps)
					{
						int tx = trace % size;
						int tz = trace / size;
						outNodes.Add(new PathFinderNode { X = tx, Z = tz });
						trace = _pfParentXZ[trace];
						guard++;
					}
				}
				return true;
			}

			steps++;
			int cx = curXZ % size;
			int cz = curXZ / size;
			int curG = _pfGScore[curXZ];

			// 严格对齐原版 PathFinder.cs (第 108-118 行) mDirection 顺序：先正交 4 方向，后对角 4 方向
			for (int dirIdx = 0; dirIdx < 8; dirIdx++)
			{
				{
					int dx = _pfDirX[dirIdx];
					int dz = _pfDirZ[dirIdx];
					int nx = cx + dx;
					int nz = cz + dz;
					if (nx < 0 || nz < 0 || nx >= size || nz >= size)
					{
						continue;
					}
					int nextXZ = nx + nz * size;
					if (_pfClosedId[nextXZ] == runId)
					{
						continue;
					}
					if (!CanStepBetweenAdjacentCells(cells, cx, cz, nx, nz))
					{
						continue;
					}
					Cell nc = cells[nx, nz];
					if (IsForbiddenDeepWater(nc, pc))
					{
						continue;
					}
					// 严格避开途中所有已发现和未发现的地面陷阱
					if (EffectiveAvoidTraps && HasTrapOnCell(nc, pc))
					{
						continue;
					}

					int stepCost = ((dx != 0 && dz != 0) ? 14 : 10);
					if (nc.hasDoor)
					{
						stepCost += 4;
					}
					if (nc.detail != null && nc.detail.charas.Count > 0)
					{
						Point np = new Point(nx, nz);
						if (IsTileBlockedByNonReplaceableChara(pc, np))
						{
							if (!allowPassThroughHostile)
							{
								continue;
							}
							// 允许把走廊中的敌对怪物视为高权重可突破格（走近后由近身自卫自动清理）
							stepCost += 120;
						}
						else
						{
							// 同伴占位格附加轻微代价，优先绕行空地，狭窄走廊则直接与同伴换位通过
							stepCost += (combat ? 35 : 15);
						}
					}

					int tentativeG = curG + stepCost;
					if (_pfVisitId[nextXZ] != runId || tentativeG < _pfGScore[nextXZ])
					{
						_pfVisitId[nextXZ] = runId;
						_pfGScore[nextXZ] = tentativeG;
						_pfParentXZ[nextXZ] = curXZ;
						int adx = Mathf.Abs(nx - dest.x);
						int adz = Mathf.Abs(nz - dest.z);
						int h = 10 * (adx + adz) - 6 * ((adx < adz) ? adx : adz);
						HeapPush(nextXZ, tentativeG + h);
					}
				}
			}
		}
		return false;
	}

	/// <summary>供 SafeAI_Goto 填充安全路径节点（完全避开明暗陷阱与禁入深水区）。</summary>
	private bool PopulateSafePathForGoto(Chara owner, PathProgress progress, Point dest, int destDist, bool ignoreConnection)
	{
		if (owner == null || progress == null || dest == null || !dest.IsValid)
		{
			return false;
		}
		Point start = ((Card)owner).pos;
		if (start == null || !start.IsValid)
		{
			return false;
		}
		progress.startPoint = start.Copy();
		progress.destPoint = dest.Copy();
		progress.destDist = destDist;
		progress.ignoreConnection = ignoreConnection;
		progress.nodes.Clear();

		Point actualDest = dest;
		if (destDist > 0 || dest.IsBlocked || (EffectiveAvoidTraps && HasTrapOnCell(dest.cell, owner)) || IsForbiddenDeepWater(dest.cell, owner))
		{
			if (!TryGetReachableInteractStandPoint(owner, dest, allowStandOnTarget: destDist == 0 && !dest.IsBlocked, combat: false, out var bestStand, allowPassThroughHostile: true) || bestStand == null)
			{
				progress.state = PathProgress.State.Fail;
				return false;
			}
			actualDest = bestStand;
		}

		if (TryFindSafePath(owner, start, actualDest, combat: false, allowPassThroughHostile: false, buildNodes: true, progress.nodes)
			|| TryFindSafePath(owner, start, actualDest, combat: false, allowPassThroughHostile: true, buildNodes: true, progress.nodes))
		{
			progress.nodeIndex = progress.nodes.Count - 1;
			progress.state = ((progress.nodes.Count > 0) ? PathProgress.State.PathReady : PathProgress.State.Idle);
			return progress.nodes.Count > 0 || start.Equals(actualDest);
		}

		progress.state = PathProgress.State.Fail;
		return false;
	}

	/// <summary>安全移动 AI_Goto 子类：
	/// 1. 始终使用 TryFindSafePath 计算出的避障路径（绕开所有可见与不可见陷阱 TraitTrap/TraitSwitch 及禁入深水区）；
	/// 2. 彻底移除原版 AI_Goto.Run() 第 207 行因 EClass.player.enemySpotted 导致的半途中断；
	/// 3. 开门（MoveResult.Door）后继续前进，不视为失败；
	/// 4. 将基类 AI_Goto.dest 置为 (-1,-1)，彻底阻断 TraitNewZone.OnStepped(Chara c) 中 aI_Goto.dest.Equals(owner.pos) 自动上下楼梯，确保停在楼梯格上。</summary>
	private class SafeAI_Goto : AI_Goto
	{
		private readonly AutoController _ctrl;
		private readonly Point _targetDest = new Point();
		private readonly bool _isDungeonNavStairGoto;

		// 严格依据 Card.DamageHP (Card.cs:4783) 与 AI_Goto.cs:22-24：
		// 1) CancelWhenDamaged => false：防止移动/追怪/风筝/采集走位途中受到 1 点伤害（怪摸一下、流血、中毒、陷阱等）就被原版 DamageHP 强行 TryCancel 打断；
		// 2) CancelWhenMoved => false：防止同伴换位 MoveByForce (Chara.cs:2628) 时打断移动；
		// 3) CancelOnAggro => false：防止遭遇仇恨打断。
		public override bool CancelWhenDamaged => false;

		public override bool CancelWhenMoved => false;

		public override bool CancelOnAggro => false;

		public SafeAI_Goto(AutoController ctrl, Point _dest, int _destDist = 0, bool _ignoreConnection = false, bool _interaction = false, bool isDungeonNavStairGoto = false)
			: base(_dest, _destDist, _ignoreConnection, _interaction)
		{
			_ctrl = ctrl;
			_isDungeonNavStairGoto = isDungeonNavStairGoto;
			if (_dest != null)
			{
				_targetDest.Set(_dest);
			}
			// 关键：依据 TraitNewZone.OnStepped 第 266-270 行与 Chara.Tick 第 3964-3968 行，
			// 原版仅当 aI_Goto.dest.Equals(owner.pos) 时才会触发自动进出楼梯 MoveZone()。
			// 将基类字段 dest 置为 (-1, -1)，内部寻路统一使用 _targetDest，从而使角色踩上楼梯格时绝不自动上下楼！
			dest.Set(-1, -1);
		}

		public override Point GetDestination()
		{
			if (destCard != null)
			{
				return destCard.pos;
			}
			return _targetDest;
		}

		private bool IsSafeDestinationReached()
		{
			if (owner == null || ((Card)owner).pos == null || !_targetDest.IsValid)
			{
				return false;
			}
			int num = Util.Distance(((Card)owner).pos.x, ((Card)owner).pos.z, _targetDest.x, _targetDest.z);
			if (interaction && num == 1 && owner.CanInteractTo(_targetDest))
			{
				return true;
			}
			return num <= destDist;
		}

		public override IEnumerable<Status> Run()
		{
			if (owner == null || owner.host != null || _ctrl == null)
			{
				yield return Cancel();
				yield break;
			}
			if (destCard != null && !interaction)
			{
				_targetDest.Set(destCard.pos);
			}
			dest.Set(-1, -1);
			if (_targetDest == null || !_targetDest.IsValid)
			{
				yield return Cancel();
				yield break;
			}
			if (IsSafeDestinationReached())
			{
				if (_isDungeonNavStairGoto && _ctrl.IsDungeonNavigating)
				{
					_ctrl.CompleteDungeonNavOnArrival(owner, calledFromInsideGoto: true);
				}
				yield return Success();
				yield break;
			}

			PathProgress path = owner.path;
			if (path == null || !_ctrl.PopulateSafePathForGoto(owner, path, _targetDest, destDist, ignoreConnection))
			{
				yield return Cancel();
				yield break;
			}

			int repathCount = 0;
			Point nextPt = new Point();
			while (true)
			{
				AM_Adv.actCount = 0;
				dest.Set(-1, -1);
				if (EClass.player != null)
				{
					EClass.player.enemySpotted = false;
					EClass.player.haltMove = false;
					if (owner.IsPC && EClass.player.TooHeavyToMove())
					{
						yield return Cancel();
						yield break;
					}
				}
				if (destCard != null && !interaction)
				{
					_targetDest.Set(destCard.pos);
				}
				if (_ctrl.IsDungeonNavigating)
				{
					_ctrl.TryPickAdjacentItemsImmediate(owner);
				}
				if (IsSafeDestinationReached())
				{
					if (_isDungeonNavStairGoto && _ctrl.IsDungeonNavigating)
					{
						_ctrl.CompleteDungeonNavOnArrival(owner, calledFromInsideGoto: true);
					}
					yield return Success();
					yield break;
				}
				// 地牢一键寻路途中，若沿途遇到新刷新的怪或沿路可拾取道具，立即交回主循环去打怪/捡道具
				if (_isDungeonNavStairGoto && _ctrl.IsDungeonNavigating && _ctrl.ShouldInterruptDungeonNavWalkForCombatOrLoot(owner))
				{
					_ctrl.RequestImmediateTick();
					yield return Success();
					yield break;
				}

				if (path.nodeIndex < 0 || path.nodes.Count == 0 || path.nodeIndex >= path.nodes.Count)
				{
					if (repathCount >= 4 || !_ctrl.PopulateSafePathForGoto(owner, path, _targetDest, destDist, ignoreConnection))
					{
						yield return Cancel();
						yield break;
					}
					repathCount++;
				}

				if (owner.IsPC && ActionMode.IsAdv && path.nodeIndex > 2)
				{
					ActionMode.Adv.SetTurbo();
				}

				PathFinderNode node = path.nodes[path.nodeIndex];
				nextPt.Set(node.X, node.Z);
				if (nextPt.x == owner.pos.x && nextPt.z == owner.pos.z && path.nodeIndex > 0 && !nextPt.cell.HasLadder && !owner.Cell.HasLadder)
				{
					path.nodeIndex--;
					node = path.nodes[path.nodeIndex];
					nextPt.Set(node.X, node.Z);
				}

				// 关键修复：Chara.CanMoveTo(Point p) (Chara.cs:2441) 内部不检查 pos.Distance(p) <= 1！
				// 若玩家在上一回合被同伴换位 MoveByForce 或被击退导致 owner.pos 与 nextPt 距离 > 1（或 == 0），
				// 直接调用 TryMove(nextPt) 会导致单回合跨两格瞬移/滑步飘逸！此时必须立即重算路径确保每步严格只走相邻 1 格。
				int stepDist = owner.pos.Distance(nextPt);
				if (stepDist != 1)
				{
					if (IsSafeDestinationReached())
					{
						yield return Success();
						yield break;
					}
					if (repathCount >= 4 || !_ctrl.PopulateSafePathForGoto(owner, path, _targetDest, destDist, ignoreConnection))
					{
						yield return Cancel();
						yield break;
					}
					repathCount++;
					continue;
				}

				// 每迈出一步前再次校验下一格是否有陷阱（哪怕是隐藏陷阱）或禁入深水区
				if (!nextPt.IsValid || !nextPt.IsInBounds
					|| (_ctrl.EffectiveAvoidTraps && _ctrl.HasTrapOnCell(nextPt.cell, owner))
					|| _ctrl.IsForbiddenDeepWater(nextPt.cell, owner))
				{
					if (repathCount >= 4 || !_ctrl.PopulateSafePathForGoto(owner, path, _targetDest, destDist, ignoreConnection))
					{
						yield return Cancel();
						yield break;
					}
					repathCount++;
					continue;
				}

				// 若下一格恰有敌对生物挡路，直接移交主循环触发近身战斗
				if (nextPt.HasChara)
				{
					Chara firstChara = nextPt.FirstChara;
					if (firstChara != null && firstChara != owner && firstChara.IsHostile())
					{
						_ctrl.RequestImmediateTick();
						yield return Success();
						yield break;
					}
				}

				dest.Set(-1, -1);
				Card.MoveResult moveResult = owner.TryMove(nextPt);
				if (owner == null)
				{
					yield return Status.Running;
					continue;
				}
				if (_ctrl.IsDungeonNavigating)
				{
					_ctrl.TryPickAdjacentItemsImmediate(owner);
				}
				if (moveResult == Card.MoveResult.Door || moveResult == Card.MoveResult.Event)
				{
					// 打开了门或触发了地形事件，若尚未移入目标格则保持当前 nodeIndex 下一回合继续走入
					if (owner.pos.Equals(nextPt))
					{
						path.nodeIndex--;
					}
					yield return Status.Running;
					continue;
				}
				if (moveResult == Card.MoveResult.Fail || !owner.pos.Equals(nextPt))
				{
					if (repathCount >= 4 || !_ctrl.PopulateSafePathForGoto(owner, path, _targetDest, destDist, ignoreConnection))
					{
						yield return Cancel();
						yield break;
					}
					repathCount++;
					yield return Status.Running;
					continue;
				}

				path.nodeIndex--;
				if (IsSafeDestinationReached())
				{
					if (_isDungeonNavStairGoto && _ctrl.IsDungeonNavigating)
					{
						_ctrl.CompleteDungeonNavOnArrival(owner, calledFromInsideGoto: true);
					}
					yield return Success();
					yield break;
				}
				if (_isDungeonNavStairGoto && _ctrl.IsDungeonNavigating && _ctrl.ShouldInterruptDungeonNavWalkForCombatOrLoot(owner))
				{
					_ctrl.RequestImmediateTick();
					yield return Success();
					yield break;
				}
				yield return Status.Running;
			}
		}
	}

	/// <summary>
	/// 安全自动战斗 AI（继承自 GoalCombat，完整对齐 GoalAutoCombat.cs 的技能/远程/近战逻辑，同时规避 AM_Adv._OnUpdateInput 第 496-513 行
	/// 因同伴濒死 abortOnAllyDying、半血 abortOnHalfHP 或原版濒死判定而每帧强行 Cancel() 导致玩家在怪面前罚站停摆的问题）。
	/// 低血量风筝（KiteHpPercent）与撤退（RetreatHpPercent）完全由 AutoController 主循环统一接管。
	/// </summary>
	private class SafeGoalAutoCombat : GoalCombat
	{
		private readonly AutoController _ctrl;
		public Thing renderThing;

		public override bool UseTurbo => EClass.game.config.autoCombat.turbo;

		public override bool CancelWhenDamaged => false;

		public override bool CancelOnAggro => false;

		public override Thing RenderThing => renderThing;

		public ConfigAutoCombat config => EClass.game.config.autoCombat;

		public SafeGoalAutoCombat(AutoController ctrl, Chara e)
		{
			_ctrl = ctrl;
			destEnemy = e;
			if (EClass.player != null && EClass.pc != null)
			{
				EClass.player.autoCombatStartHP = ((EClass.pc.Evalue(1421) > 0) ? (EClass.pc.hp + EClass.pc.mana.value) : EClass.pc.hp);
			}
		}

		public override Color GetActPlanColor()
		{
			if (!EClass.pc.IsCriticallyWounded())
			{
				if (!EClass.pc.party.IsCriticallyWounded())
				{
					return EClass.Colors.colorAct;
				}
				return EClass.Colors.colorActWarnning;
			}
			return EClass.Colors.colorActCriticalWarning;
		}

		public override bool TryUseRanged(int dist)
		{
			Thing ranged = owner.ranged;
			owner.ranged = null;
			if (config.bUseHotBar)
			{
				FindRanged(hotbar: true);
			}
			if (owner.ranged != null && ACT.Ranged.Perform(owner, tc))
			{
				if (owner == null)
				{
					return true;
				}
				renderThing = owner.ranged;
				if (owner.ranged != ranged)
				{
					owner.ranged = ranged;
				}
				return true;
			}
			return false;
			void FindRanged(bool hotbar)
			{
				owner.things.Foreach(delegate(Thing t)
				{
					if (t.IsHotItem)
					{
						if (!hotbar)
						{
							return false;
						}
					}
					else if (hotbar)
					{
						return false;
					}
					if (t.IsRangedWeapon && (t.trait is TraitToolRangeCane || t.trait is TraitToolRangeSling || t.c_ammo > 0 || EClass.pc.FindAmmo(t) != null) && owner.CanEquipRanged(t))
					{
						owner.ranged = t;
						return true;
					}
					return false;
				});
			}
		}

		public void GetAbilities(Func<Element, bool> func)
		{
			if (config.bUseHotBar)
			{
				foreach (Thing item in EClass.pc.things.Where((Thing t) => t.IsHotItem && t.trait is TraitAbility))
				{
					Element element = owner.elements.GetElement(item.c_idAbility);
					if (element != null && func(element) && (element.id != 9150 || EClass.player.favAbility.Contains(element.id)))
					{
						AddAbility(element.act, 15);
					}
				}
			}
			if (!config.bUseInventory)
			{
				return;
			}
			foreach (Element value in owner.elements.dict.Values)
			{
				if ((!config.bUseFav || EClass.player.favAbility.Contains(value.id)) && func(value) && (value.id != 9150 || EClass.player.favAbility.Contains(value.id)))
				{
					AddAbility(value.act);
				}
			}
		}

		public override void BuildAbilityList()
		{
			GetAbilities((Element e) => e.source.abilityType.Length != 0);
			AddAbility(ACT.Ranged);
			AddAbility(ACT.Melee);
			AddAbility(ACT.Item);
			if (owner.HasCondition<ConBrightnessOfLife>())
			{
				TryAddAbility(6410);
			}
		}

		public override bool TryUseAbility(int dist, bool beforeMove = false)
		{
			if (owner != null && owner.IsPC)
			{
				// 对齐原版 GoalCombat.cs 第 196-197 行：保持自动战斗加速并给予策略技能 (135) 经验
				if (ActionMode.IsAdv && ActionMode.Adv != null)
				{
					ActionMode.Adv.SetTurbo(EClass.game.config.autoCombat.turbo ? (-1) : 0);
				}
				owner.ModExp(135, 20);
			}
			return base.TryUseAbility(dist, beforeMove);
		}

		public override bool TryAbortCombat()
		{
			if (tc == null || tc.isDead || !tc.ExistsOnMap)
			{
				return true;
			}
			// 若敌人退开不再处于无隔墙直接贴身位，交回 AutoController 用 SafeAI_Goto 追击（避开陷阱与深水）
			if (owner != null && owner.IsPC && _ctrl != null && !_ctrl.CanDirectlyAttackEnemy(owner, tc))
			{
				_ctrl.RequestImmediateTick();
				return true;
			}
			if (idleCount >= 2)
			{
				return true;
			}
			ConDeathSentense condition = owner.GetCondition<ConDeathSentense>();
			if (condition != null && condition.value <= 3)
			{
				Msg.Say("abort_sentense");
				return true;
			}
			return false;
		}
	}

	/// <summary>检测玩家能否精确寻路到达某个可落脚空格 standPos（destDist = 0），并保证途中不踩任何明暗陷阱或禁入深水区。</summary>
	private bool CanExactPathToStandPoint(Chara pc, Point standPos, bool combat, bool allowPassThroughHostile = false)
	{
		if (pc == null || standPos == null || !standPos.IsValid)
		{
			return false;
		}
		return TryGetStandTilePathDistance(pc, standPos.x, standPos.z, allowPassThroughHostile, out _);
	}

	private bool IsValidExactPath(PathProgress progress, Point dest)
	{
		if (progress == null || !progress.HasPath || progress.nodes == null || progress.nodes.Count == 0)
		{
			return false;
		}
		PathFinderNode endNode = progress.nodes[0];
		return endNode.X == dest.x && endNode.Z == dest.z;
	}

	/// <summary>为目标敌人寻找一个与敌人同侧、无隔墙/斜角墙阻挡、无任何明暗陷阱且玩家【真实寻路路程最近】的攻击落脚格（destDist=0）。
	/// 支持 allowPassThroughHostile 回退：当走廊中有前排怪物挡路时，不误把后排残血怪判为不可达。</summary>
	private bool TryGetReachableAttackStandPoint(Chara pc, Chara enemy, out Point bestStand, out float bestPathDist, bool allowPassThroughHostile = false)
	{
		bestStand = null;
		bestPathDist = float.MaxValue;
		if (pc == null || enemy == null || enemy.isDead)
		{
			return false;
		}
		Point pcPos = ((Card)pc).pos;
		Point ePos = ((Card)enemy).pos;
		if (pcPos == null || ePos == null || !ePos.IsValid || !ePos.IsInBounds)
		{
			return false;
		}
		if (CanDirectlyAttackEnemy(pc, enemy) && !(EffectiveAvoidTraps && HasTrapOnCell(pcPos.cell, pc)))
		{
			bestStand = pcPos.Copy();
			bestPathDist = ((pcPos.x != ePos.x && pcPos.z != ePos.z) ? 1.4f : 1.0f);
			return true;
		}

		// 第一轮：优先在无需穿过其他敌对生物的可达邻格中，选出【真实行走路程最短】的落脚格
		Point bestStrict = null;
		float bestStrictScore = float.MaxValue;
		float bestStrictRawDist = float.MaxValue;

		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				if (dx == 0 && dz == 0)
				{
					continue;
				}
				Point n = new Point(ePos.x + dx, ePos.z + dz);
				if (!n.IsValid || !n.IsInBounds || n.IsBlocked)
				{
					continue;
				}
				Cell nc = n.cell;
				if (nc == null || nc.outOfBounds || IsForbiddenDeepWater(nc, pc))
				{
					continue;
				}
				if (EffectiveAvoidTraps && HasTrapOnCell(nc, pc))
				{
					continue;
				}
				// 关键：落脚格 n 必须与敌人所在格 ePos 之间无隔墙、无斜角墙阻挡且有视线
				if (!CanInteractBetweenPoints(n, ePos, allowBlockedTarget: false))
				{
					continue;
				}
				if (!IsTileBlockedByNonReplaceableChara(pc, n) && TryGetStandTilePathDistance(pc, n.x, n.z, allowPassThroughHostile: false, out var pDist))
				{
					float score = pDist + ((dx != 0 && dz != 0) ? 0.2f : 0f) + ((n.HasChara && !n.Equals(pcPos)) ? 0.45f : 0f);
					if (score < bestStrictScore)
					{
						bestStrictScore = score;
						bestStrictRawDist = pDist + 1f;
						bestStrict = n;
					}
				}
			}
		}

		if (bestStrict != null)
		{
			bestStand = bestStrict;
			bestPathDist = bestStrictRawDist;
			return true;
		}

		if (!allowPassThroughHostile)
		{
			return false;
		}

		// 第二轮（窄道多怪兜底）：允许穿过走廊中前排挡路怪到达后排目标邻格
		Point bestPass = null;
		float bestPassScore = float.MaxValue;
		float bestPassRawDist = float.MaxValue;

		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				if (dx == 0 && dz == 0)
				{
					continue;
				}
				Point n = new Point(ePos.x + dx, ePos.z + dz);
				if (!n.IsValid || !n.IsInBounds || n.IsBlocked)
				{
					continue;
				}
				Cell nc = n.cell;
				if (nc == null || nc.outOfBounds || IsForbiddenDeepWater(nc, pc))
				{
					continue;
				}
				if (EffectiveAvoidTraps && HasTrapOnCell(nc, pc))
				{
					continue;
				}
				if (!CanInteractBetweenPoints(n, ePos, allowBlockedTarget: false))
				{
					continue;
				}
				if (TryGetStandTilePathDistance(pc, n.x, n.z, allowPassThroughHostile: true, out var pDist))
				{
					float score = pDist + ((dx != 0 && dz != 0) ? 0.2f : 0f) + ((n.HasChara && !n.Equals(pcPos)) ? 0.45f : 0f);
					if (score < bestPassScore)
					{
						bestPassScore = score;
						bestPassRawDist = pDist + 1f;
						bestPass = n;
					}
				}
			}
		}

		if (bestPass != null)
		{
			bestStand = bestPass;
			bestPathDist = bestPassRawDist;
			return true;
		}
		return false;
	}

	private bool TryGetReachableAttackStandPoint(Chara pc, Chara enemy, out Point bestStand, bool allowPassThroughHostile = false)
	{
		return TryGetReachableAttackStandPoint(pc, enemy, out bestStand, out _, allowPassThroughHostile);
	}

	/// <summary>为采集物或地面拾取物寻找一个无隔墙/无斜角墙阻挡、无任何明暗陷阱且【真实寻路路程最近】（destDist=0）的交互落脚格。</summary>
	private bool TryGetReachableInteractStandPoint(Chara pc, Point targetPos, bool allowStandOnTarget, bool combat, out Point bestStand, out float bestPathDist, bool allowPassThroughHostile = false)
	{
		bestStand = null;
		bestPathDist = float.MaxValue;
		if (pc == null || targetPos == null || !targetPos.IsValid || !targetPos.IsInBounds)
		{
			return false;
		}
		Point pcPos = ((Card)pc).pos;
		if (pcPos == null)
		{
			return false;
		}
		bool pcOnTrap = EffectiveAvoidTraps && HasTrapOnCell(pcPos.cell, pc);
		if (pcPos.Equals(targetPos) && allowStandOnTarget && !pcOnTrap)
		{
			bestStand = pcPos.Copy();
			bestPathDist = 0f;
			return true;
		}
		if (!pcOnTrap && pcPos.Distance(targetPos) == 1 && CanInteractBetweenPoints(pcPos, targetPos, allowBlockedTarget: targetPos.IsBlocked))
		{
			bestStand = pcPos.Copy();
			bestPathDist = ((pcPos.x != targetPos.x && pcPos.z != targetPos.z) ? 0.9f : 0.7f);
			return true;
		}

		Point bestCand = null;
		float bestScore = float.MaxValue;
		float bestRawDist = float.MaxValue;

		// 评估直接踩在目标格上（如拾取空地上的掉落物，或站在草药格上采集）
		Cell targetCell = targetPos.cell;
		if (allowStandOnTarget && !targetPos.IsBlocked && targetCell != null && !targetCell.outOfBounds
			&& !IsForbiddenDeepWater(targetCell, pc) && !(EffectiveAvoidTraps && HasTrapOnCell(targetCell, pc))
			&& !IsTileBlockedByNonReplaceableChara(pc, targetPos)
			&& TryGetStandTilePathDistance(pc, targetPos.x, targetPos.z, allowPassThroughHostile: false, out var onTargetDist))
		{
			bestScore = onTargetDist;
			bestRawDist = onTargetDist;
			bestCand = targetPos.Copy();
		}

		// 第一轮：评估目标周围 8 个相邻格中无需穿过敌对怪物的真实最短路径落脚格
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				if (dx == 0 && dz == 0)
				{
					continue;
				}
				Point n = new Point(targetPos.x + dx, targetPos.z + dz);
				if (!n.IsValid || !n.IsInBounds || n.IsBlocked)
				{
					continue;
				}
				Cell nc = n.cell;
				if (nc == null || nc.outOfBounds || IsForbiddenDeepWater(nc, pc))
				{
					continue;
				}
				if (EffectiveAvoidTraps && HasTrapOnCell(nc, pc))
				{
					continue;
				}
				if (!CanInteractBetweenPoints(n, targetPos, allowBlockedTarget: targetPos.IsBlocked))
				{
					continue;
				}
				if (!IsTileBlockedByNonReplaceableChara(pc, n) && TryGetStandTilePathDistance(pc, n.x, n.z, allowPassThroughHostile: false, out var pDist))
				{
					float score = pDist + 0.5f + ((dx != 0 && dz != 0) ? 0.2f : 0f) + ((n.HasChara && !n.Equals(pcPos)) ? 0.45f : 0f);
					if (score < bestScore)
					{
						bestScore = score;
						bestRawDist = pDist + 0.7f;
						bestCand = n;
					}
				}
			}
		}

		if (bestCand != null)
		{
			bestStand = bestCand;
			bestPathDist = bestRawDist;
			return true;
		}

		if (!allowPassThroughHostile)
		{
			return false;
		}

		// 第二轮（允许穿过走廊中的敌对生物）
		if (allowStandOnTarget && !targetPos.IsBlocked && targetCell != null && !targetCell.outOfBounds
			&& !IsForbiddenDeepWater(targetCell, pc) && !(EffectiveAvoidTraps && HasTrapOnCell(targetCell, pc))
			&& TryGetStandTilePathDistance(pc, targetPos.x, targetPos.z, allowPassThroughHostile: true, out var onTargetPassDist))
		{
			bestScore = onTargetPassDist;
			bestRawDist = onTargetPassDist;
			bestCand = targetPos.Copy();
		}

		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				if (dx == 0 && dz == 0)
				{
					continue;
				}
				Point n = new Point(targetPos.x + dx, targetPos.z + dz);
				if (!n.IsValid || !n.IsInBounds || n.IsBlocked)
				{
					continue;
				}
				Cell nc = n.cell;
				if (nc == null || nc.outOfBounds || IsForbiddenDeepWater(nc, pc))
				{
					continue;
				}
				if (EffectiveAvoidTraps && HasTrapOnCell(nc, pc))
				{
					continue;
				}
				if (!CanInteractBetweenPoints(n, targetPos, allowBlockedTarget: targetPos.IsBlocked))
				{
					continue;
				}
				if (TryGetStandTilePathDistance(pc, n.x, n.z, allowPassThroughHostile: true, out var pDist))
				{
					float score = pDist + 0.5f + ((dx != 0 && dz != 0) ? 0.2f : 0f) + ((n.HasChara && !n.Equals(pcPos)) ? 0.45f : 0f);
					if (score < bestScore)
					{
						bestScore = score;
						bestRawDist = pDist + 0.7f;
						bestCand = n;
					}
				}
			}
		}

		if (bestCand != null)
		{
			bestStand = bestCand;
			bestPathDist = bestRawDist;
			return true;
		}
		return false;
	}

	private bool TryGetReachableInteractStandPoint(Chara pc, Point targetPos, bool allowStandOnTarget, bool combat, out Point bestStand, bool allowPassThroughHostile = false)
	{
		return TryGetReachableInteractStandPoint(pc, targetPos, allowStandOnTarget, combat, out bestStand, out _, allowPassThroughHostile);
	}

	/// <summary>检测玩家当前位置能否安全寻路到达目标点（或其无隔墙、无陷阱相邻格），并返回真实寻路距离。</summary>
	private bool TryGetTargetPathDistance(Chara pc, Point targetPos, int destDist, bool combat, bool allowPassThroughHostile, out float pathDist)
	{
		pathDist = float.MaxValue;
		if (pc == null || targetPos == null || !targetPos.IsValid)
		{
			return false;
		}
		Point pcPos = ((Card)pc).pos;
		if (pcPos == null)
		{
			return false;
		}
		bool pcOnTrap = EffectiveAvoidTraps && HasTrapOnCell(pcPos.cell, pc);
		int tileDist = pcPos.Distance(targetPos);
		if (tileDist == 0 && !pcOnTrap)
		{
			pathDist = 0f;
			return true;
		}
		if (!pcOnTrap && tileDist <= Mathf.Max(1, destDist))
		{
			if (CanInteractBetweenPoints(pcPos, targetPos, allowBlockedTarget: targetPos.IsBlocked))
			{
				pathDist = ((pcPos.x != targetPos.x && pcPos.z != targetPos.z) ? 0.9f : 0.7f);
				return true;
			}
		}
		bool allowStandOnTarget = (destDist == 0 || !targetPos.IsBlocked) && !(EffectiveAvoidTraps && HasTrapOnCell(targetPos.cell, pc));
		return TryGetReachableInteractStandPoint(pc, targetPos, allowStandOnTarget, combat, out _, out pathDist, allowPassThroughHostile);
	}

	/// <summary>检测玩家当前位置能否安全寻路到达目标点（或其无隔墙、无陷阱相邻格）。</summary>
	private bool CanPathToTarget(Chara pc, Point targetPos, int destDist = 1, bool combat = false, bool allowPassThroughHostile = false)
	{
		return TryGetTargetPathDistance(pc, targetPos, destDist, combat, allowPassThroughHostile, out _);
	}

	private void ResetProgressAnchor(Chara pc = null)
	{
		_stuckAutoTimer = 0f;
		if (pc == null)
		{
			pc = EClass.pc;
		}
		if (pc != null && ((Card)pc).pos != null)
		{
			_anchorPosX = ((Card)pc).pos.x;
			_anchorPosZ = ((Card)pc).pos.z;
		}
	}

	private void Start()
	{
		LoadConfig();
		ResetTempWallOptions();
		_debugEnabled = DebugMode;
	}

	/// <summary>严格依据 AI_Fish.cs / AIWork_Fish.cs / AIAct.cs 检测玩家当前是否处于钓鱼行动中。</summary>
	public static bool IsPlayerFishing(Chara pc)
	{
		if (pc == null || pc.isDead || pc.HasNoGoal || pc.ai == null || !pc.ai.IsRunning)
		{
			return false;
		}
		for (AIAct act = pc.ai; act != null; act = act.child)
		{
			if (act is AI_Fish || act is AI_Fish.ProgressFish || act is AIWork_Fish)
			{
				return true;
			}
			if (!act.IsChildRunning)
			{
				break;
			}
		}
		return false;
	}

	/// <summary>
	/// 钓鱼掉血保护：仅在检测到玩家启动钓鱼、处于钓鱼过程中时才启动保护，平时绝不介入（不影响玩家正常游玩）。
	/// 钓鱼期间一旦检测到掉血（无论是饥饿 AttackSource.Hunger、负重 AttackSource.Burden、还是体力负数 AttackSource.Fatigue 等），立刻打断钓鱼。
	/// </summary>
	private void UpdateFishingProtection()
	{
		try
		{
			if (!FishingProtectionEnabled)
			{
				_fishingGuardActive = false;
				_fishingLastHp = int.MinValue;
				_lastNonFishingHp = int.MinValue;
				_lastNonFishingMap = null;
				return;
			}
			Chara pc = EClass.pc;
			if (pc == null || EClass._map == null || EClass._zone == null || pc.isDead)
			{
				_fishingGuardActive = false;
				_fishingLastHp = int.MinValue;
				_lastNonFishingHp = int.MinValue;
				_lastNonFishingMap = null;
				return;
			}
			int curHp = ((Card)pc).hp;
			if (!IsPlayerFishing(pc))
			{
				_fishingGuardActive = false;
				_fishingLastHp = int.MinValue;
				_lastNonFishingHp = curHp;
				_lastNonFishingMap = EClass._map;
				return;
			}

			if (!_fishingGuardActive)
			{
				_fishingGuardActive = true;
				_fishingLastHp = (_lastNonFishingMap == EClass._map && _lastNonFishingHp > 0) ? _lastNonFishingHp : curHp;
				Log("Fishing protection activated: baseline HP=" + _fishingLastHp + ", current HP=" + curHp);
				if (curHp < _fishingLastHp)
				{
					Log("Fishing interrupted immediately on start due to HP loss: " + _fishingLastHp + " -> " + curHp);
					_fishingGuardActive = false;
					_fishingLastHp = int.MinValue;
					_lastNonFishingHp = curHp;
					InterruptPlayerFishing(pc);
					return;
				}
				_fishingLastHp = curHp;
				return;
			}

			if (curHp < _fishingLastHp)
			{
				Log("Fishing interrupted due to HP loss: " + _fishingLastHp + " -> " + curHp);
				_fishingGuardActive = false;
				_fishingLastHp = int.MinValue;
				_lastNonFishingHp = curHp;
				InterruptPlayerFishing(pc);
			}
			else if (curHp > _fishingLastHp)
			{
				_fishingLastHp = curHp;
			}
		}
		catch (Exception ex)
		{
			Log("UpdateFishingProtection error: " + ex.Message);
		}
	}

	private void InterruptPlayerFishing(Chara pc)
	{
		if (pc == null || !IsPlayerFishing(pc))
		{
			return;
		}
		AM_Adv.actCount = 0;
		AI_Fish.shouldCancel = false;
		if (ActionMode.Adv != null)
		{
			if (ActionMode.Adv.pressedAction != null && (ActionMode.Adv.pressedAction.button != null || ActionMode.Adv.pressedAction.action != null))
			{
				ActionMode.Adv.pressedAction.Init();
			}
			ActionMode.Adv.TryCancelInteraction(sound: true);
			ActionMode.Adv.EndTurbo();
		}
		if (pc.ai != null && pc.ai.IsRunning)
		{
			((Card)pc).Say("cancel_act_pc", (Card)pc, (string)null, (string)null);
			pc.ai.Cancel();
		}
		if (!pc.HasNoGoal)
		{
			pc.SetNoGoal();
		}
		AI_Fish.shouldCancel = false;
	}

	public void RequestImmediateTick()
	{
		_tickTimer = _tickInterval;
	}

	private void LateUpdate()
	{
		UpdateFishingProtection();
	}

	private void Update()
	{
		UpdateFishingProtection();

		if (Input.GetKey((KeyCode)308) && Input.GetKeyDown((KeyCode)113))
		{
			if (IsDungeonNavigating && !Enabled)
			{
				StopDungeonNav(cancelPCAct: true);
			}
			else
			{
				ToggleEnabled();
			}
		}
		if (!Enabled && !IsDungeonNavigating)
		{
			CurrentState = State.Idle;
			_stuckAutoTimer = 0f;
			_idleAutoTimer = 0f;
			return;
		}
		if (EClass.pc == null || EClass._map == null || EClass._zone == null)
		{
			return;
		}
		// 进入大地图（Region）时立即自动关闭脚本，彻底防止在世界大地图上乱走
		if (((Spatial)EClass._zone).IsRegion)
		{
			Log("Auto-disable: entered world region map (大地图自动停止)");
			Enabled = false;
			StopDungeonNav(cancelPCAct: false);
			CleanupState();
			ClearPickupState();
			return;
		}
		if (EClass.pc.isDead)
		{
			Enabled = false;
			StopDungeonNav(cancelPCAct: false);
			CleanupState();
			ClearPickupState();
			return;
		}

		// 保持清除原版右键连按计数、发现敌人打断标记与强制停步标记，防止 TaskPoint.Run() 篡改 pos 或 Chara.Tick (3977行) / AI_Goto 半途莫名中断
		AM_Adv.actCount = 0;
		if (EClass.player != null)
		{
			EClass.player.enemySpotted = false;
			EClass.player.haltMove = false;
		}

		Chara pc = EClass.pc;
		int x = ((Card)pc).pos.x;
		int z = ((Card)pc).pos.z;
		bool moved = x != _lastPosX || z != _lastPosZ;
		if (moved)
		{
			_lastPosX = x;
			_lastPosZ = z;
		}
		if (_anchorPosX == int.MinValue || _anchorPosZ == int.MinValue)
		{
			_anchorPosX = x;
			_anchorPosZ = z;
		}

		// 基于锚点位移 (> 4.5 格) 与实际交互进度重置卡死计时，避免在地图边界或小范围正常采收/拾取/风筝时误触发 10 秒自动关停
		float distFromAnchor = GetDistance(x, z, _anchorPosX, _anchorPosZ);
		bool progressedFarFromAnchor = distFromAnchor > 4.5f;
		if (progressedFarFromAnchor)
		{
			_anchorPosX = x;
			_anchorPosZ = z;
		}

		bool activelyHarvestingAdjacent = IsBusyGathering(pc) && (pc.ai is TaskHarvest || pc.ai is TaskMine) && _lastGatherTarget != null && GetDistance(x, z, _lastGatherTarget.x, _lastGatherTarget.z) <= 1.6f && CanInteractBetweenPoints(((Card)pc).pos, _lastGatherTarget, allowBlockedTarget: _lastGatherTarget.IsBlocked);
		// 处于有效战斗（直接近战/远程 GoalCombat）或风筝走位产生真实位移时均视为有效战斗进度
		bool activelyFighting = _currentCombatTarget != null && !_currentCombatTarget.isDead && ((CanDirectlyAttackEnemy(pc, _currentCombatTarget) && (pc.ai is GoalCombat || GetDistance(pc, _currentCombatTarget) <= 1.6f)) || (CurrentState == State.Kiting && moved));

		if (progressedFarFromAnchor || activelyHarvestingAdjacent || activelyFighting)
		{
			_stuckAutoTimer = 0f;
			if (activelyHarvestingAdjacent || activelyFighting)
			{
				_anchorPosX = x;
				_anchorPosZ = z;
			}
		}
		else if (CurrentState == State.Gathering || CurrentState == State.Idle || CurrentState == State.Picking || CurrentState == State.Combat || CurrentState == State.Kiting)
		{
			_stuckAutoTimer += Time.deltaTime;
		}

		if (CurrentState == State.Idle && !moved)
		{
			_idleAutoTimer += Time.deltaTime;
		}
		else
		{
			_idleAutoTimer = 0f;
		}

		if (IdleAutoStopEnabled)
		{
			float timeout = Mathf.Max(3f, (float)IdleAutoStopSeconds);
			if (_idleAutoTimer >= timeout || _stuckAutoTimer >= timeout)
			{
				Log("Auto-disable: idle/stuck for " + timeout + "s (IdleTimer=" + _idleAutoTimer.ToString("0.0") + "s, StuckTimer=" + _stuckAutoTimer.ToString("0.0") + "s)");
				Enabled = false;
				StopDungeonNav(cancelPCAct: true);
				CleanupState();
				ClearPickupState();
				return;
			}
		}

		float num = (IsDungeonNavigating ? 0.18f : ((CurrentState == State.Idle) ? _idleTickInterval : _tickInterval));
		_tickTimer += Time.deltaTime;
		if (!(_tickTimer < num))
		{
			_tickTimer = 0f;
			Tick();
		}
	}

	/// <summary>统一的启停入口，Alt+Q 快捷键和UI里的"运行/停止"按钮都走这里，
	/// 保证不管从哪个入口关闭，残留状态与临时挖墙开关都会被清干净。</summary>
	public void ToggleEnabled()
	{
		Enabled = !Enabled;
		if (!Enabled)
		{
			StopDungeonNav(cancelPCAct: true);
			CleanupState();
			ClearPickupState();
		}
		else
		{
			StopDungeonNav(cancelPCAct: false);
			_stuckAutoTimer = 0f;
			_idleAutoTimer = 0f;
			_lastPosX = int.MinValue;
			_lastPosZ = int.MinValue;
			_anchorPosX = int.MinValue;
			_anchorPosZ = int.MinValue;
			_dungeonMinedWallCount = 0;
			_lastMineWallTarget = null;
			_isSwimRecoveringOxygen = false;
			_failedTargets.Clear();
			_unreachableGatherTargets.Clear();
			_gatherFailCounts.Clear();
			_pickupFailCounts.Clear();
			_enemyFailCounts.Clear();
			_failedEnemies.Clear();
			_lastCombatStandTarget = null;
			_combatStuckTicks = 0;
			ClearPickupState();
		}
		Log("Toggle: Enabled=" + Enabled);
	}

	private void CleanupState()
	{
		CurrentState = State.Idle;
		_retreatTarget = null;
		_lastShoreTarget = null;
		_isSwimRecoveringOxygen = false;
		_lastTrapStandTarget = null;
		_lastEquippedSlot = -1;
		_failedTargets.Clear();
		_unreachableGatherTargets.Clear();
		_gatherFailCounts.Clear();
		_pickupFailCounts.Clear();
		_enemyFailCounts.Clear();
		_lastGatherTarget = null;
		_gatherRetryCount = 0;
		_lastMineWallTarget = null;
		_currentCombatTarget = null;
		_failedEnemies.Clear();
		_lastCombatTargetUid = -1;
		_combatMoveRetryCount = 0;
		_lastCombatTargetDist = float.MaxValue;
		_lastCombatStandTarget = null;
		_combatStuckTicks = 0;
		_lastCombatPosX = int.MinValue;
		_lastCombatPosZ = int.MinValue;
		_stuckAutoTimer = 0f;
		_idleAutoTimer = 0f;
		_lastPosX = int.MinValue;
		_lastPosZ = int.MinValue;
		_anchorPosX = int.MinValue;
		_anchorPosZ = int.MinValue;
		_tickTimer = 0f;
		ResetTempWallOptions();
		Log("State cleaned up & temp wall options reset");
	}

	private void ClearGatherProgressTracking()
	{
		_lastGatherTarget = null;
		_gatherRetryCount = 0;
		_lastTrapStandTarget = null;
		if (_lastMineWallTarget != null && _lastMineWallTarget.HasBlock)
		{
			_lastMineWallTarget = null;
		}
	}

	/// <summary>严格校验玩家当前 AIAct 是否真实处于 Running 状态（解决 Chara.Tick 末尾子动作失败或完成后 pc.ai 仍残留旧对象引用的问题）。</summary>
	private bool IsAIRunning(Chara pc)
	{
		return pc != null && pc.ai != null && !pc.HasNoGoal && !(pc.ai is NoGoal) && pc.ai.IsRunning && pc.ai.status == AIAct.Status.Running;
	}

	private bool IsRunningGoto(Chara pc)
	{
		return IsAIRunning(pc) && pc.ai is AI_Goto;
	}

	/// <summary>安全启动玩家 AIAct：
	/// 1. 强制清零 AM_Adv.actCount，阻断 TaskPoint.Run() 第 74-90 行将 pos 篡改为鼠标位置或直接 Destroy()；
	/// 2. 强制清零 EClass.player.enemySpotted 与 haltMove，防止 AI_Goto.Run() 第 207 行或 Chara.Tick(3977行) 被旧标记打断 Cancel()；
	/// 3. 调用 SetAIImmediate 前先确保 HasNoGoal 为 true（对齐以前的高速响应逻辑与 Chara.SetAIImmediate 第 8895 行）；
	/// 4. 调用后严格校验 IsAIRunning(pc)。</summary>
	private bool StartPCActImmediate(Chara pc, AIAct act)
	{
		if (pc == null || act == null)
		{
			return false;
		}
		AM_Adv.actCount = 0;
		if (EClass.player != null)
		{
			EClass.player.enemySpotted = false;
			EClass.player.haltMove = false;
		}
		if (!pc.HasNoGoal)
		{
			pc.SetNoGoal();
		}
		pc.SetAIImmediate(act);
		return IsAIRunning(pc);
	}

	/// <summary>启动绕开所有明暗陷阱与禁入深水区的安全移动 SafeAI_Goto。</summary>
	private bool StartSafeGoto(Chara pc, Point dest, int destDist = 0, bool ignoreConnection = false, bool isDungeonNavStairGoto = false)
	{
		if (pc == null || dest == null || !dest.IsValid)
		{
			return false;
		}
		return StartPCActImmediate(pc, new SafeAI_Goto(this, dest, destDist, ignoreConnection, false, isDungeonNavStairGoto)) && pc.ai is AI_Goto;
	}

	private bool IsBusyGathering(Chara pc)
	{
		return IsAIRunning(pc) && (pc.ai is TaskHarvest || pc.ai is TaskMine || (CurrentState == State.Gathering && pc.ai is AI_Goto));
	}

	/// <summary>自动按地图环境判定当前是否处于地牢（EClass._zone is Zone_Dungeon），不再受 UI 处于哪个设置分页影响。</summary>
	public bool IsInDungeonContext()
	{
		return EClass._zone != null && EClass._zone is Zone_Dungeon;
	}

	/// <summary>是否处于完全无视深水区状态（开启了"直接无视深水区"开关、位于水下地图、或角色自身拥有 429 水下呼吸能力）。</summary>
	private bool CanFreelyWalkDeepWater(Chara pc)
	{
		if (IgnoreDeepWater)
		{
			return true;
		}
		if (EClass._zone != null && EClass._zone.IsUnderwater)
		{
			return true;
		}
		if (pc != null && ((Card)pc).HasElement(429, false))
		{
			return true;
		}
		return false;
	}

	/// <summary>判定某格是否为原始深水格（无桥、未结冰、无浮台且会导致窒息的深水区）。</summary>
	private bool IsRawSuffocatingDeepWater(Cell cell)
	{
		if (cell == null)
		{
			return false;
		}
		if (EClass._zone != null && EClass._zone.IsUnderwater)
		{
			return false;
		}
		return cell.CanSuffocate();
	}

	/// <summary>在未开启"直接无视深水区"且未开启"游泳模式"（或游泳模式正在岸上换气）时，深水格视为禁入区。</summary>
	private bool IsForbiddenDeepWater(Cell cell, Chara pc)
	{
		if (CanFreelyWalkDeepWater(pc))
		{
			return false;
		}
		if (SwimModeEnabled && !_isSwimRecoveringOxygen && GetCurrentOxygenPercent(pc) > SwimRetreatOxygenPercent)
		{
			return false;
		}
		return IsRawSuffocatingDeepWater(cell);
	}

	/// <summary>检查某格是否存在任何负面陷阱（TraitTrap / TraitTrapMine / TraitTrapSister / TraitBananaPeel / 负面 TraitSwitch），
	/// 无论该陷阱是否已被发现（包含 isHidden == true 或 isMasked == true 的肉眼不可见陷阱）一律检出并避开！</summary>
	private bool HasTrapOnCell(Cell cell, Chara pc)
	{
		if (cell == null || cell.detail == null || cell.detail.things == null || cell.detail.things.Count == 0)
		{
			return false;
		}
		bool isPlayerZone = EClass._zone != null && (EClass._zone.IsPCFaction || EClass._zone.IsPCFactionOrTent || EClass._zone.IsUserZone);
		List<Thing> things = cell.detail.things;
		for (int i = 0; i < things.Count; i++)
		{
			Thing t = things[i];
			if (t == null || ((Card)t).isDestroyed || !((Card)t).IsInstalled)
			{
				continue;
			}
			if (t.trait is TraitSwitch ts && ts.IsNegativeEffect && !ts.IsJammed)
			{
				// 玩家自己领地内布置的地雷/香蕉皮对玩家与同伴不触发（对齐 TraitTrapMine.CanActivateTrap）
				if (isPlayerZone && (ts is TraitTrapMine || ts is TraitBananaPeel))
				{
					continue;
				}
				if (pc != null && pc.IsLevitating && ts.IgnoreWhenLevitating())
				{
					continue;
				}
				return true;
			}
		}
		return false;
	}

	/// <summary>兼容方法：检查某格是否存在任何明暗陷阱（包括肉眼看不见的隐藏陷阱）。</summary>
	private bool HasVisibleTrapOnCell(Cell cell, Chara pc)
	{
		return HasTrapOnCell(cell, pc);
	}

	/// <summary>处理深水区紧急上岸与游泳模式的氧气管控。</summary>
	private bool TryEmergencyAshore(Chara pc)
	{
		if (CanFreelyWalkDeepWater(pc))
		{
			_isSwimRecoveringOxygen = false;
			_lastShoreTarget = null;
			return false;
		}

		Cell pcCell = ((Card)pc).pos?.cell;
		bool inDeepWater = IsRawSuffocatingDeepWater(pcCell);
		int currentOxy = GetCurrentOxygenPercent(pc);

		if (SwimModeEnabled)
		{
			if (currentOxy <= SwimRetreatOxygenPercent)
			{
				_isSwimRecoveringOxygen = true;
			}
			else if (!inDeepWater && currentOxy >= 100)
			{
				_isSwimRecoveringOxygen = false;
			}

			if (!_isSwimRecoveringOxygen)
			{
				_lastShoreTarget = null;
				return false;
			}

			if (!inDeepWater)
			{
				_lastShoreTarget = null;
				if (currentOxy < 100 && (pc.ai == null || pc.ai is NoGoal))
				{
					pc.TickConditions();
				}
				return false;
			}

			ClearGatherProgressTracking();
			_stuckAutoTimer = 0f;
			_idleAutoTimer = 0f;

			Point swimShore = FindNearestSafeShorePoint(pc);
			if (swimShore != null)
			{
				if (!IsRunningGoto(pc) || _lastShoreTarget == null || !_lastShoreTarget.Equals(swimShore))
				{
					_lastShoreTarget = swimShore.Copy();
					StartSafeGoto(pc, swimShore, 0, false);
					Log("Swim mode: oxygen low (" + currentOxy + "% <= " + SwimRetreatOxygenPercent + "%), swimming ashore to (" + swimShore.x + "," + swimShore.z + ")");
				}
			}
			return true;
		}

		if (!inDeepWater)
		{
			_lastShoreTarget = null;
			if (currentOxy < 100 && !IsAIRunning(pc))
			{
				pc.TickConditions();
			}
			return false;
		}

		if (_lastGatherTarget != null)
		{
			_failedTargets.Add(_lastGatherTarget.x + "," + _lastGatherTarget.z);
			TrimFailedTargets();
			ClearGatherProgressTracking();
		}
		if (targetPickup != null)
		{
			failedPickups[((Card)targetPickup).uid] = Time.time + 30f;
			targetPickup = null;
		}

		_stuckAutoTimer = 0f;
		_idleAutoTimer = 0f;

		Point shore = FindNearestSafeShorePoint(pc);
		if (shore != null)
		{
			if (!IsRunningGoto(pc) || _lastShoreTarget == null || !_lastShoreTarget.Equals(shore))
			{
				_lastShoreTarget = shore.Copy();
				StartSafeGoto(pc, shore, 0, false);
				Log("Emergency ashore: retreating from deep water to (" + shore.x + "," + shore.z + ")");
			}
		}
		return true;
	}

	private Point FindNearestSafeShorePoint(Chara pc)
	{
		if (EClass._map == null)
		{
			return null;
		}
		GetGatherScanBounds(out var boundMinX, out var boundMaxX, out var boundMinZ, out var boundMaxZ);
		int px = ((Card)pc).pos.x;
		int pz = ((Card)pc).pos.z;
		Point best = null;
		float bestDist = float.MaxValue;

		for (int r = 1; r <= 30; r++)
		{
			int minX = Mathf.Max(boundMinX, px - r);
			int maxX = Mathf.Min(boundMaxX, px + r);
			int minZ = Mathf.Max(boundMinZ, pz - r);
			int maxZ = Mathf.Min(boundMaxZ, pz + r);
			for (int x = minX; x <= maxX; x++)
			{
				for (int z = minZ; z <= maxZ; z++)
				{
					if (Mathf.Abs(x - px) != r && Mathf.Abs(z - pz) != r)
					{
						continue;
					}
					Point pt = new Point(x, z);
					if (!pt.IsValid || !pt.IsInBounds || pt.IsBlocked)
					{
						continue;
					}
					Cell c = pt.cell;
					if (c == null || c.outOfBounds || IsRawSuffocatingDeepWater(c))
					{
						continue;
					}
					if (EffectiveAvoidTraps && HasVisibleTrapOnCell(c, pc))
					{
						continue;
					}
					if (!TryGetStandTilePathDistance(pc, x, z, allowPassThroughHostile: true, out var d))
					{
						continue;
					}
					if (d < bestDist)
					{
						bestDist = d;
						best = pt;
					}
				}
			}
			if (best != null)
			{
				return best;
			}
		}
		return null;
	}

	/// <summary>尝试执行全图/设定范围内的寻怪与战斗（用于近身无怪时按【怪优先 / 捡优先】调度）。</summary>
	private bool TryExecuteCombatScanAndEngage(Chara pc, float hpRatio, float maxRadiusOverride = -1f, bool forceFullMap = false)
	{
		Chara targetEnemy = FindBestEnemy(pc, maxRadiusOverride, forceFullMap);
		if (targetEnemy == null)
		{
			return false;
		}
		if (hpRatio < KiteHpPercent)
		{
			CurrentState = State.Kiting;
			_currentCombatTarget = targetEnemy;
			ClearGatherProgressTracking();
			DoKite(pc, targetEnemy);
			return true;
		}
		_currentCombatTarget = targetEnemy;
		if (DoCombat(pc, targetEnemy))
		{
			CurrentState = State.Combat;
			ClearGatherProgressTracking();
			return true;
		}
		_currentCombatTarget = null;
		return false;
	}

	/// <summary>尝试执行全图可拾取物扫描与前往拾取（用于近身无怪时按【怪优先 / 捡优先】调度）。</summary>
	private bool TryExecutePickupScanAndStep(Chara pc, bool effectiveAutoPick, bool forceImmediateScan = false)
	{
		if (!effectiveAutoPick)
		{
			if (CurrentState == State.Picking)
			{
				CurrentState = State.Idle;
			}
			return false;
		}
		if (CurrentState == State.Picking)
		{
			if (HandlePickingStep(pc))
			{
				return true;
			}
		}
		if (forceImmediateScan || Time.time >= nextPickScanTime)
		{
			nextPickScanTime = Time.time + 1.0f;
			targetPickup = ScanForPickup();
			if (targetPickup != null)
			{
				CurrentState = State.Picking;
				ClearGatherProgressTracking();
				if (HandlePickingStep(pc))
				{
					return true;
				}
			}
		}
		return false;
	}

	/// <summary>状态栏【一键寻路：出口 / 下一层】切换入口。</summary>
	public void ToggleDungeonNav(DungeonNavTarget target)
	{
		if (target == DungeonNavTarget.None || CurrentDungeonNav == target)
		{
			StopDungeonNav(cancelPCAct: true);
		}
		else
		{
			StartDungeonNav(target);
		}
	}

	public bool StartDungeonNav(DungeonNavTarget target)
	{
		if (target == DungeonNavTarget.None || EClass.pc == null || EClass._map == null || EClass._zone == null)
		{
			return false;
		}
		if (((Spatial)EClass._zone).IsRegion || EClass.pc.isDead)
		{
			return false;
		}
		Chara pc = EClass.pc;
		CurrentDungeonNav = target;
		InvalidateDistanceFieldCache();
		if (!TryFindDungeonNavTarget(pc, target, out var stairThing, out var stairPos, out var standPos) || stairPos == null || standPos == null)
		{
			CurrentDungeonNav = DungeonNavTarget.None;
			InvalidateDistanceFieldCache();
			string targetLabel = (target == DungeonNavTarget.Exit) ? "当层出口" : "下一层入口";
			Msg.SayRaw("当前层未找到可达的" + targetLabel);
			return false;
		}
		Point pcPos = ((Card)pc).pos;
		if (pcPos != null && (pcPos.Equals(stairPos) || pcPos.Equals(standPos)))
		{
			CurrentDungeonNav = DungeonNavTarget.None;
			InvalidateDistanceFieldCache();
			string targetLabel = (target == DungeonNavTarget.Exit) ? "当层出口" : "下一层入口";
			Msg.SayRaw("已在" + targetLabel + "位置");
			return false;
		}
		// 启动地牢一键寻路时关闭常规全图采集，保证到达楼梯格后停在原地并自动结束寻路
		Enabled = false;
		CleanupState();
		ClearPickupState();
		CurrentDungeonNav = target;
		_lastDungeonNavStairStand = null;
		_dungeonNavRetryCount = 0;
		_lastMap = EClass._map;
		InvalidateDistanceFieldCache();
		_tickTimer = _tickInterval;
		Log("Started DungeonNav: target=" + target + " stair=(" + stairPos.x + "," + stairPos.z + ")");
		return true;
	}

	public void StopDungeonNav(bool cancelPCAct = false)
	{
		bool wasNav = CurrentDungeonNav != DungeonNavTarget.None;
		CurrentDungeonNav = DungeonNavTarget.None;
		_lastDungeonNavStairStand = null;
		_dungeonNavRetryCount = 0;
		InvalidateDistanceFieldCache();
		if (cancelPCAct && EClass.pc != null)
		{
			AM_Adv.actCount = 0;
			if (ActionMode.Adv != null)
			{
				ActionMode.Adv.EndTurbo();
			}
			if (EClass.pc.ai != null && EClass.pc.ai.IsRunning)
			{
				EClass.pc.ai.Cancel();
			}
			if (!EClass.pc.HasNoGoal)
			{
				EClass.pc.SetNoGoal();
			}
		}
		if (!Enabled)
		{
			CurrentState = State.Idle;
			_stuckAutoTimer = 0f;
			_idleAutoTimer = 0f;
		}
		if (wasNav)
		{
			Log("Stopped DungeonNav");
		}
	}

	/// <summary>到达目标楼梯格后停在楼梯格上并自动结束寻路。</summary>
	private void CompleteDungeonNavOnArrival(Chara pc, bool calledFromInsideGoto)
	{
		DungeonNavTarget finishedTarget = CurrentDungeonNav;
		if (pc != null)
		{
			TryPickAdjacentItemsImmediate(pc);
		}
		CurrentDungeonNav = DungeonNavTarget.None;
		Enabled = false;
		_lastDungeonNavStairStand = null;
		_dungeonNavRetryCount = 0;
		InvalidateDistanceFieldCache();
		AM_Adv.actCount = 0;
		if (ActionMode.Adv != null)
		{
			ActionMode.Adv.EndTurbo();
		}
		CleanupState();
		ClearPickupState();
		if (!calledFromInsideGoto && pc != null)
		{
			if (pc.ai != null && pc.ai.IsRunning)
			{
				pc.ai.Cancel();
			}
			if (!pc.HasNoGoal)
			{
				pc.SetNoGoal();
			}
		}
		if (finishedTarget != DungeonNavTarget.None)
		{
			string msg = (finishedTarget == DungeonNavTarget.Exit) ? "已到达当层出口" : "已到达下一层入口";
			Msg.SayRaw(msg);
			Log("DungeonNav completed on stairs: " + msg);
		}
	}

	/// <summary>
	/// 严格依据 TraitNewZone.cs (第 191-201 行)、TraitStairsDown.cs、TraitStairsUp.cs、TraitStairsLocked.cs 与 Zone.cs：
	/// - 地下地牢 (zone.lv &lt;= 0)：当层出口 = IsUpstairs (TraitStairsUp)；下一层入口 = IsDownstairs (TraitStairsDown) 或 TraitStairsLocked。
	/// - 地上塔类地牢 (zone.lv &gt; 0)：当层出口 = IsDownstairs (TraitStairsDown)；下一层入口 = IsUpstairs (TraitStairsUp) 或 TraitStairsLocked。
	/// 优先直接以楼梯所在格为落脚点，保证到达后停在楼梯格上。
	/// </summary>
	private bool TryFindDungeonNavTarget(Chara pc, DungeonNavTarget navTarget, out Thing bestThing, out Point bestStairPos, out Point bestStandPos)
	{
		bestThing = null;
		bestStairPos = null;
		bestStandPos = null;
		if (pc == null || EClass._map == null || EClass._zone == null || EClass._map.things == null || navTarget == DungeonNavTarget.None)
		{
			return false;
		}
		EnsureSafeDistanceField(pc);
		bool isSkyZone = ((Spatial)EClass._zone).lv > 0;
		Point pcPos = ((Card)pc).pos;

		float bestScore = float.MaxValue;
		for (int pass = 0; pass < 2; pass++)
		{
			foreach (Thing thing in EClass._map.things)
			{
				if (thing == null || ((Card)thing).isDestroyed || !((Card)thing).ExistsOnMap || !((Card)thing).IsInstalled || thing.trait == null)
				{
					continue;
				}
				bool matched = false;
				if (navTarget == DungeonNavTarget.NextFloor)
				{
					if (pass == 0)
					{
						if (thing.trait is TraitNewZone nz)
						{
							matched = (isSkyZone ? nz.IsUpstairs : nz.IsDownstairs);
						}
						else if (thing.trait is TraitStairsLocked)
						{
							matched = true;
						}
					}
				}
				else if (navTarget == DungeonNavTarget.Exit)
				{
					if (pass == 0)
					{
						if (thing.trait is TraitNewZone nz)
						{
							matched = (isSkyZone ? nz.IsDownstairs : nz.IsUpstairs);
						}
					}
					else
					{
						if (thing.trait is TraitNewZone nz2 && nz2.IsEntrace && !nz2.IsTeleport && !(nz2 is TraitElevator))
						{
							matched = true;
						}
					}
				}
				if (!matched)
				{
					continue;
				}

				Point sPos = ((Card)thing).pos;
				if (sPos == null || !sPos.IsValid || !sPos.IsInBounds || sPos.cell == null || sPos.cell.outOfBounds)
				{
					continue;
				}
				if (pcPos != null && pcPos.Equals(sPos))
				{
					bestThing = thing;
					bestStairPos = sPos.Copy();
					bestStandPos = sPos.Copy();
					return true;
				}

				Point candStand = null;
				float candDist = float.MaxValue;
				// 优先直接落脚在楼梯格本身（停在楼梯格上）
				if (!sPos.IsBlocked && !IsForbiddenDeepWater(sPos.cell, pc) && !(EffectiveAvoidTraps && HasTrapOnCell(sPos.cell, pc)))
				{
					if (!IsTileBlockedByNonReplaceableChara(pc, sPos) && TryGetStandTilePathDistance(pc, sPos.x, sPos.z, allowPassThroughHostile: false, out var strictD))
					{
						candStand = sPos.Copy();
						candDist = strictD;
					}
					else if (TryGetStandTilePathDistance(pc, sPos.x, sPos.z, allowPassThroughHostile: true, out var passD))
					{
						candStand = sPos.Copy();
						candDist = passD + 2f;
					}
				}
				// 若楼梯格本身被非敌对不可置换物体占满或为阻挡格，回退到楼梯相邻交互格
				if (candStand == null && TryGetReachableInteractStandPoint(pc, sPos, allowStandOnTarget: !sPos.IsBlocked, combat: false, out var adjStand, out var adjDist, allowPassThroughHostile: true))
				{
					candStand = adjStand;
					candDist = adjDist + 5f;
				}
				if (candStand != null && candDist < bestScore)
				{
					bestScore = candDist;
					bestThing = thing;
					bestStairPos = sPos.Copy();
					bestStandPos = candStand;
				}
			}
			if (bestThing != null)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>地牢一键寻路途中，顺手拾取玩家脚下或紧邻 1 格内（无隔墙/无陷阱阻挡）的地面道具。</summary>
	private void TryPickAdjacentItemsImmediate(Chara pc)
	{
		if (pc == null || EClass._map == null || EClass._map.things == null)
		{
			return;
		}
		Point pcPos = ((Card)pc).pos;
		if (pcPos == null || !pcPos.IsValid)
		{
			return;
		}
		float now = Time.time;
		int pickedCount = 0;
		for (int i = EClass._map.things.Count - 1; i >= 0 && pickedCount < 8; i--)
		{
			Thing thing = EClass._map.things[i];
			if (thing == null || ((Card)thing).isDestroyed || !((Card)thing).ExistsOnMap || (int)((Card)thing).placeState != 0 || ((Card)thing).ignoreAutoPick || ((Card)thing).isMasked || ((Card)thing).isHidden)
			{
				continue;
			}
			if (thing.trait != null && !string.IsNullOrEmpty(thing.trait.ReqHarvest))
			{
				continue;
			}
			Point pos = ((Card)thing).pos;
			if (pos == null || !pos.IsValid || pcPos.Distance(pos) > 1 || GetDistance(pcPos.x, pcPos.z, pos.x, pos.z) >= 1.5f)
			{
				continue;
			}
			if (!CanInteractBetweenPoints(pcPos, pos, allowBlockedTarget: pos.IsBlocked))
			{
				continue;
			}
			int uid = ((Card)thing).uid;
			if (failedPickups.TryGetValue(uid, out var failTime) && (now < failTime || now - failTime < 3f))
			{
				continue;
			}
			if (!CanPickThingToInventory(pc, thing))
			{
				failedPickups[uid] = now + 5f;
				continue;
			}
			if (TryPickupItem(thing))
			{
				_pickupFailCounts.Remove(uid);
				pickedCount++;
				ResetProgressAnchor(pc);
			}
			else
			{
				RecordPickupFailure(uid);
			}
		}
	}

	/// <summary>地牢一键寻路途中，检测沿路是否有需要停下处理的敌对怪物或可拾取地面道具。</summary>
	private bool ShouldInterruptDungeonNavWalkForCombatOrLoot(Chara pc)
	{
		if (pc == null || EClass._map == null)
		{
			return false;
		}
		if (FindBestEnemy(pc, DungeonNavCombatRadius, forceFullMap: false) != null)
		{
			return true;
		}
		if (ScanForPickup(DungeonNavPickupRadius) != null)
		{
			return true;
		}
		return false;
	}

	/// <summary>地牢一键寻路途中扫描并拾取沿路 (DungeonNavPickupRadius 内) 的地面道具。</summary>
	private bool TryExecuteDungeonNavPickup(Chara pc)
	{
		if (CurrentState == State.Picking)
		{
			if (HandlePickingStep(pc))
			{
				return true;
			}
		}
		targetPickup = ScanForPickup(DungeonNavPickupRadius);
		if (targetPickup != null)
		{
			CurrentState = State.Picking;
			ClearGatherProgressTracking();
			if (HandlePickingStep(pc))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>地牢一键寻路主调度：默认自行避开陷阱，路途碰上怪自动打、碰上地面道具自动捡，到达楼梯格停在上面并结束寻路。</summary>
	private void TickDungeonNav(Chara pc)
	{
		if (pc == null || EClass._map == null || EClass._zone == null)
		{
			return;
		}
		if (TryEmergencyAshore(pc))
		{
			return;
		}
		float hpRatio = (float)((Card)pc).hp / (float)((Card)pc).MaxHP;
		if (hpRatio < RetreatHpPercent)
		{
			CurrentState = State.Retreating;
			ClearGatherProgressTracking();
			DoRetreat(pc);
			return;
		}
		if (_currentCombatTarget != null && _currentCombatTarget.isDead)
		{
			nextPickScanTime = 0f;
			_currentCombatTarget = null;
			_lastCombatTargetUid = -1;
			_combatMoveRetryCount = 0;
			_lastCombatStandTarget = null;
			_combatStuckTicks = 0;
			ResetProgressAnchor(pc);
		}

		// 顺手拾取脚下或相邻 1 格的道具
		TryPickAdjacentItemsImmediate(pc);

		// 1) 路途遇到怪（或新刷新的怪）优先战斗
		if (TryExecuteCombatScanAndEngage(pc, hpRatio, DungeonNavCombatRadius, forceFullMap: false))
		{
			_lastDungeonNavStairStand = null;
			return;
		}

		// 2) 拾取沿路地面道具
		if (TryExecuteDungeonNavPickup(pc))
		{
			_lastDungeonNavStairStand = null;
			return;
		}

		// 3) 寻路前往当层出口 / 下一层入口，到达楼梯格后停在楼梯格上并自动结束寻路
		if (!TryFindDungeonNavTarget(pc, CurrentDungeonNav, out var stairThing, out var stairPos, out var standPos) || stairPos == null || standPos == null)
		{
			string targetLabel = (CurrentDungeonNav == DungeonNavTarget.Exit) ? "当层出口" : "下一层入口";
			Msg.SayRaw("未找到可达的" + targetLabel + "，已结束寻路");
			StopDungeonNav(cancelPCAct: true);
			return;
		}

		Point pcPos = ((Card)pc).pos;
		if (pcPos != null && (pcPos.Equals(stairPos) || pcPos.Equals(standPos)))
		{
			CompleteDungeonNavOnArrival(pc, calledFromInsideGoto: false);
			return;
		}

		// 若目标楼梯格上正站着敌对生物，优先上前清理
		if (stairPos.HasChara)
		{
			List<Chara> stairCharas = stairPos.Charas;
			if (stairCharas != null)
			{
				for (int i = 0; i < stairCharas.Count; i++)
				{
					Chara sc = stairCharas[i];
					if (sc != null && sc != pc && !sc.isDead && sc.IsHostile())
					{
						_currentCombatTarget = sc;
						if (DoCombat(pc, sc))
						{
							CurrentState = State.Combat;
							_lastDungeonNavStairStand = null;
							return;
						}
						_currentCombatTarget = null;
					}
				}
			}
		}

		CurrentState = State.Idle;
		if (!IsRunningGoto(pc) || _lastDungeonNavStairStand == null || !_lastDungeonNavStairStand.Equals(standPos))
		{
			_lastDungeonNavStairStand = standPos.Copy();
			if (!StartSafeGoto(pc, standPos, 0, ignoreConnection: false, isDungeonNavStairGoto: true))
			{
				_dungeonNavRetryCount++;
				if (pcPos != null && pcPos.Distance(stairPos) <= 1 && CanInteractBetweenPoints(pcPos, stairPos, allowBlockedTarget: true))
				{
					CompleteDungeonNavOnArrival(pc, calledFromInsideGoto: false);
					return;
				}
				_lastDungeonNavStairStand = null;
				if (_dungeonNavRetryCount >= 4)
				{
					string targetLabel = (CurrentDungeonNav == DungeonNavTarget.Exit) ? "当层出口" : "下一层入口";
					Msg.SayRaw("前往" + targetLabel + "路径受阻，已结束寻路");
					StopDungeonNav(cancelPCAct: true);
				}
			}
			else
			{
				_dungeonNavRetryCount = 0;
			}
		}
	}

	private void Tick()
	{
		Chara pc = EClass.pc;
		if (EClass._map != _lastMap)
		{
			bool hadLastMap = _lastMap != null;
			_lastMap = EClass._map;
			_failedTargets.Clear();
			_unreachableGatherTargets.Clear();
			_gatherFailCounts.Clear();
			_pickupFailCounts.Clear();
			_enemyFailCounts.Clear();
			_failedEnemies.Clear();
			_lastCombatTargetUid = -1;
			_combatMoveRetryCount = 0;
			_lastCombatTargetDist = float.MaxValue;
			_lastCombatStandTarget = null;
			_combatStuckTicks = 0;
			_lastCombatPosX = int.MinValue;
			_lastCombatPosZ = int.MinValue;
			_anchorPosX = int.MinValue;
			_anchorPosZ = int.MinValue;
			ClearGatherProgressTracking();
			_lastMineWallTarget = null;
			_dungeonMinedWallCount = 0;
			_isSwimRecoveringOxygen = false;
			InvalidateDistanceFieldCache();
			ClearPickupState();
			if (hadLastMap && IsDungeonNavigating)
			{
				StopDungeonNav(cancelPCAct: false);
			}
			Log("Map changed, cleared gather & pickup caches");
			if (!Enabled && !IsDungeonNavigating)
			{
				return;
			}
		}

		if (IsDungeonNavigating)
		{
			TickDungeonNav(pc);
			return;
		}

		// 检查上一格敲墙目标是否已被成功挖穿
		if (_lastMineWallTarget != null && !_lastMineWallTarget.HasBlock)
		{
			if (IsInDungeonContext() && DungeonMineWalls)
			{
				_dungeonMinedWallCount++;
				Log("Dungeon wall mined: " + _dungeonMinedWallCount + "/" + DungeonMineWallLimit);
			}
			// 挖穿墙体后清空不可达缓存与寻路距离场缓存，使新打通房间内的物品、怪物与采集物立即可被扫描
			InvalidateDistanceFieldCache();
			_unreachableGatherTargets.Clear();
			_gatherFailCounts.Clear();
			failedPickups.Clear();
			_pickupFailCounts.Clear();
			_failedEnemies.Clear();
			_enemyFailCounts.Clear();
			_lastMineWallTarget = null;
			ResetProgressAnchor(pc);
		}

		// 优先第 0 级：溺水/误入深水或游泳氧气不足时的上岸保护
		if (TryEmergencyAshore(pc))
		{
			return;
		}

		float num = (float)((Card)pc).hp / (float)((Card)pc).MaxHP;
		int value = pc.stamina.value;
		Log("=== Tick === State:" + CurrentState.ToString() + " HP:" + ((Card)pc).hp + "/" + ((Card)pc).MaxHP + " SP:" + value);
		if (num < RetreatHpPercent)
		{
			CurrentState = State.Retreating;
			ClearGatherProgressTracking();
			DoRetreat(pc);
			return;
		}
		if (_currentCombatTarget != null && _currentCombatTarget.isDead)
		{
			nextPickScanTime = 0f;
			_currentCombatTarget = null;
			_lastCombatTargetUid = -1;
			_combatMoveRetryCount = 0;
			_lastCombatStandTarget = null;
			_combatStuckTicks = 0;
			ResetProgressAnchor(pc);
		}

		// 优先第 1 级：近处有怪（<= NearThreatRadius 4.5格）时，无论设置是【怪优先】还是【捡优先】，始终优先近身自卫打怪！
		if (TryExecuteCombatScanAndEngage(pc, num, NearThreatRadius, forceFullMap: false))
		{
			return;
		}

		// 检查负重上限/超量停止与地牢敲墙上限停止
		if (StopOnBurdenEnabled)
		{
			int burdenPct = GetCurrentBurdenPercent(pc);
			if (burdenPct >= MaxBurdenPercent)
			{
				Log("Auto-disable: Burden(" + burdenPct + "%) >= MaxBurdenPercent(" + MaxBurdenPercent + "%)");
				Enabled = false;
				CleanupState();
				ClearPickupState();
				return;
			}
		}
		if (IsInDungeonContext() && DungeonMineWalls && DungeonMineWallLimit > 0 && _dungeonMinedWallCount >= DungeonMineWallLimit)
		{
			Log("Auto-disable: Dungeon mined walls reached limit (" + _dungeonMinedWallCount + "/" + DungeonMineWallLimit + ")");
			Enabled = false;
			CleanupState();
			ClearPickupState();
			return;
		}

		// 地牢环境下尝试自动拆除地面已发现的陷阱（开关开启且非玩家领地时）
		if (IsInDungeonContext() && DisarmTraps && EClass._zone != null && !EClass._zone.IsPCFaction && TryHandleTrapDisarm(pc))
		{
			CurrentState = State.Harvesting;
			return;
		}

		bool effectiveAutoPick = autoPickEnabled || IsInDungeonContext();
		// 刚结束一轮采集或处于非采集中状态时，立即允许扫描地面可拾取物
		if (CurrentState == State.Gathering && !IsBusyGathering(pc))
		{
			nextPickScanTime = 0f;
		}
		bool activelyHarvestingNow = CurrentState == State.Gathering && IsBusyGathering(pc) && (pc.ai is TaskHarvest || pc.ai is TaskMine);

		// 优先第 2 级：当近处无怪时，严格按照【怪优先 / 捡优先】决定先全图寻怪去打，还是先全图寻可拾取物去捡
		if (explorePriority == ExploreActionPriority.PickupFirst)
		{
			if (!activelyHarvestingNow && TryExecutePickupScanAndStep(pc, effectiveAutoPick, forceImmediateScan: CurrentState == State.Idle))
			{
				return;
			}
			if (TryExecuteCombatScanAndEngage(pc, num, maxRadiusOverride: -1f, forceFullMap: false))
			{
				return;
			}
		}
		else
		{
			if (TryExecuteCombatScanAndEngage(pc, num, maxRadiusOverride: -1f, forceFullMap: false))
			{
				return;
			}
			if (!activelyHarvestingNow && TryExecutePickupScanAndStep(pc, effectiveAutoPick, forceImmediateScan: CurrentState == State.Idle))
			{
				return;
			}
		}

		// 优先第 3 级：怪和可拾取物都处理完后，继续执行采集
		if (CurrentState == State.Gathering && IsBusyGathering(pc))
		{
			// 若正在走近采集点的途中（AI_Goto）且已到达与目标无隔墙相邻格，允许进入 DoGather 立即启动 TaskHarvest/TaskMine
			if (!(pc.ai is AI_Goto) || _lastGatherTarget == null || ((Card)pc).pos.Distance(_lastGatherTarget) > 1 || !CanInteractBetweenPoints(((Card)pc).pos, _lastGatherTarget, allowBlockedTarget: _lastGatherTarget.IsBlocked))
			{
				return;
			}
		}

		if (value <= MaxStamina)
		{
			Log("Auto-disable: SP(" + value + ") <= MaxStamina(" + MaxStamina + ")");
			Enabled = false;
			CleanupState();
		}
		else
		{
			bool startedGather = false;
			for (int attempt = 0; attempt < 8; attempt++)
			{
				int neededSlot = -1;
				bool isWallTarget = false;
				bool isSeedTarget = false;
				Thing specificThing = null;
				Point val2 = FindGatherTarget(pc, out neededSlot, out isWallTarget, out isSeedTarget, out specificThing);
				if (val2 == null)
				{
					break;
				}
				if (DoGather(pc, val2, neededSlot, isWallTarget, isSeedTarget, specificThing))
				{
					startedGather = true;
					break;
				}
			}
			if (startedGather)
			{
				CurrentState = State.Gathering;
			}
			else
			{
				// 当没有可采集目标、即将进入空闲时，无视扫描冷却与距离限制做一次全图兜底扫描（按怪优先/捡优先顺序）
				if (explorePriority == ExploreActionPriority.PickupFirst)
				{
					if (TryExecutePickupScanAndStep(pc, effectiveAutoPick, forceImmediateScan: true))
					{
						return;
					}
					if (TryExecuteCombatScanAndEngage(pc, num, maxRadiusOverride: -1f, forceFullMap: true))
					{
						return;
					}
				}
				else
				{
					if (TryExecuteCombatScanAndEngage(pc, num, maxRadiusOverride: -1f, forceFullMap: true))
					{
						return;
					}
					if (TryExecutePickupScanAndStep(pc, effectiveAutoPick, forceImmediateScan: true))
					{
						return;
					}
				}
				CurrentState = State.Idle;
			}
		}
	}

	private void RecordPickupFailure(int uid)
	{
		_pickupFailCounts.TryGetValue(uid, out var fails);
		fails++;
		_pickupFailCounts[uid] = fails;
		// 累计失败 >= 3 次则拉黑 180 秒，首次失败仅短暂冷却 6 秒，防止因临时被怪挡路或开门中断而把全图战利品永久拉黑
		float cooldown = ((fails >= 3) ? 180f : ((fails == 2) ? 20f : 6f));
		failedPickups[uid] = Time.time + cooldown;
	}

	/// <summary>执行一步地面拾取逻辑。若仍在向可达物品移动中则返回 true；若已全部拾取完毕或目标不可达已拉黑则返回 false。</summary>
	private bool HandlePickingStep(Chara pc)
	{
		if (targetPickup != null && (((Card)targetPickup).isDestroyed || !((Card)targetPickup).ExistsOnMap || (int)((Card)targetPickup).placeState != 0))
		{
			// 玩家踩上物品所在格时已被原版 Chara._Move (Chara.cs:3079) 自动拾取入包，必须同步重置进度锚点防止被误判为 10 秒无进展停机
			ResetProgressAnchor(pc);
			targetPickup = null;
			nextPickScanTime = Time.time;
		}
		// 仅当当前没有锁定的有效拾取物（或当前未在移动中且到达扫描时间）时才重新扫描，防止边走边换目标引发左右折返
		float pickRadiusLimit = (IsDungeonNavigating ? DungeonNavPickupRadius : (-1f));
		if (targetPickup == null || (!IsRunningGoto(pc) && Time.time >= nextPickScanTime))
		{
			nextPickScanTime = Time.time + 1.0f;
			targetPickup = ScanForPickup(pickRadiusLimit);
		}
		if (targetPickup == null)
		{
			CurrentState = State.Idle;
			_lastPickTargetUid = -1;
			_pickRetryCount = 0;
			return false;
		}

		// 若已在拾取距离 (<= 1 格且 < 1.5f) 内且无隔墙阻挡，连续拾取周围所有可达掉落物，随后立即扫描下一个
		int safetyCount = 0;
		while (targetPickup != null && safetyCount < 10)
		{
			Point pPos = ((Card)targetPickup).pos;
			if (pPos == null || ((Card)pc).pos.Distance(pPos) > 1 || GetDistance(((Card)pc).pos.x, ((Card)pc).pos.z, pPos.x, pPos.z) >= 1.5f || !CanInteractBetweenPoints(((Card)pc).pos, pPos, allowBlockedTarget: pPos.IsBlocked))
			{
				break;
			}
			safetyCount++;
			int pickedUid = ((Card)targetPickup).uid;
			bool picked = TryPickupItem(targetPickup);
			if (picked)
			{
				_pickupFailCounts.Remove(pickedUid);
				ResetProgressAnchor(pc);
			}
			else
			{
				RecordPickupFailure(pickedUid);
			}
			targetPickup = ScanForPickup(pickRadiusLimit);
		}

		if (targetPickup == null)
		{
			CurrentState = State.Idle;
			nextPickScanTime = Time.time + 0.5f;
			_lastPickTargetUid = -1;
			_pickRetryCount = 0;
			return false;
		}

		Point targetPos = ((Card)targetPickup).pos;
		bool allowStepOnItem = !targetPos.IsBlocked && !(EffectiveAvoidTraps && HasTrapOnCell(targetPos.cell, pc));
		Point pickStandPos;
		float dist;
		if (!TryGetReachableInteractStandPoint(pc, targetPos, allowStepOnItem, combat: false, out pickStandPos, out dist, allowPassThroughHostile: true))
		{
			dist = GetDistance(((Card)pc).pos.x, ((Card)pc).pos.z, targetPos.x, targetPos.z);
		}
		int uid = ((Card)targetPickup).uid;

		if (uid != _lastPickTargetUid)
		{
			_lastPickTargetUid = uid;
			_pickRetryCount = 0;
			_lastPickDist = dist;
		}

		if (!IsRunningGoto(pc))
		{
			if (_pickRetryCount > 0)
			{
				if (dist < _lastPickDist - 0.3f)
				{
					_lastPickDist = dist;
					_pickRetryCount = 1;
				}
				else
				{
					_pickRetryCount++;
				}
			}
			if (_pickRetryCount > 4 || pickStandPos == null)
			{
				RecordPickupFailure(uid);
				targetPickup = ScanForPickup(pickRadiusLimit);
				_lastPickTargetUid = -1;
				_pickRetryCount = 0;
				if (targetPickup == null)
				{
					CurrentState = State.Idle;
					return false;
				}
				targetPos = ((Card)targetPickup).pos;
				uid = ((Card)targetPickup).uid;
				allowStepOnItem = !targetPos.IsBlocked && !(EffectiveAvoidTraps && HasTrapOnCell(targetPos.cell, pc));
				if (!TryGetReachableInteractStandPoint(pc, targetPos, allowStepOnItem, combat: false, out pickStandPos, out dist, allowPassThroughHostile: true) || pickStandPos == null)
				{
					RecordPickupFailure(uid);
					targetPickup = null;
					CurrentState = State.Idle;
					return false;
				}
				_lastPickTargetUid = uid;
				_lastPickDist = dist;
			}

			_pickRetryCount++;
			// 使用 SafeAI_Goto 精确走向验证过的安全落脚格 pickStandPos (destDist = 0)，途中严格避开所有明暗陷阱
			if (!StartSafeGoto(pc, pickStandPos, 0, false))
			{
				if (((Card)pc).pos.Distance(targetPos) <= 1 && GetDistance(((Card)pc).pos.x, ((Card)pc).pos.z, targetPos.x, targetPos.z) < 1.5f && CanInteractBetweenPoints(((Card)pc).pos, targetPos, allowBlockedTarget: targetPos.IsBlocked))
				{
					if (TryPickupItem(targetPickup))
					{
						_pickupFailCounts.Remove(uid);
						ResetProgressAnchor(pc);
					}
					else
					{
						RecordPickupFailure(uid);
					}
				}
				else
				{
					RecordPickupFailure(uid);
				}
				targetPickup = null;
				CurrentState = State.Idle;
				_lastPickTargetUid = -1;
				_pickRetryCount = 0;
				return false;
			}
		}
		return true;
	}

	/// <summary>地牢环境下扫描并前往拆除地面上已发现的陷阱（站在陷阱相邻的安全格进行拆除，绝不踩到陷阱格上）。</summary>
	private bool TryHandleTrapDisarm(Chara pc)
	{
		if (EClass._map == null)
		{
			return false;
		}
		GetGatherScanBounds(out var minX, out var maxX, out var minZ, out var maxZ);
		Thing bestTrap = null;
		TraitSwitch bestTrait = null;
		Point bestStandPos = null;
		float bestDist = float.MaxValue;

		foreach (Thing thing in EClass._map.things)
		{
			if (thing == null || ((Card)thing).isDestroyed || !((Card)thing).ExistsOnMap || ((Card)thing).isHidden || ((Card)thing).isMasked || !((Card)thing).IsInstalled)
			{
				continue;
			}
			if (!(thing.trait is TraitSwitch ts) || !ts.IsNegativeEffect || !ts.CanDisarmTrap || ts.IsJammed)
			{
				continue;
			}
			Point pos = ((Card)thing).pos;
			if (pos == null || pos.x < minX || pos.x > maxX || pos.z < minZ || pos.z > maxZ)
			{
				continue;
			}
			string trapKey = "trap_" + ((Card)thing).uid;
			if (_failedTargets.Contains(trapKey))
			{
				continue;
			}
			if (!TryGetSafeAdjacentStandPoint(pos, pc, out var standPoint, out var d))
			{
				continue;
			}
			if (d < bestDist)
			{
				bestDist = d;
				bestTrap = thing;
				bestTrait = ts;
				bestStandPos = standPoint;
			}
		}

		if (bestTrap == null || bestTrait == null || bestStandPos == null)
		{
			_lastTrapStandTarget = null;
			return false;
		}

		Point trapPos = ((Card)bestTrap).pos;
		float dist = GetDistance(((Card)pc).pos.x, ((Card)pc).pos.z, trapPos.x, trapPos.z);
		bool notStandingOnTrap = ((Card)pc).pos.x != trapPos.x || ((Card)pc).pos.z != trapPos.z;

		if (dist < 1.5f && notStandingOnTrap && CanInteractBetweenPoints(((Card)pc).pos, trapPos, allowBlockedTarget: true))
		{
			_lastTrapStandTarget = null;
			ResetProgressAnchor(pc);
			bool success = bestTrait.TryDisarmTrap(pc);
			if (!success && ((Card)pc).Evalue(1656) < 3 && EClass.rnd(2) == 0)
			{
				bestTrait.ActivateTrap(pc);
			}
			if (!success && (((Card)bestTrap).isDestroyed || !bestTrait.CanDisarmTrap || bestTrait.IsJammed))
			{
				_failedTargets.Add("trap_" + ((Card)bestTrap).uid);
				TrimFailedTargets();
			}
			return true;
		}

		if (!IsRunningGoto(pc) || _lastTrapStandTarget == null || !_lastTrapStandTarget.Equals(bestStandPos))
		{
			_lastTrapStandTarget = bestStandPos.Copy();
			if (!StartSafeGoto(pc, bestStandPos, 0, false))
			{
				_failedTargets.Add("trap_" + ((Card)bestTrap).uid);
				TrimFailedTargets();
				_lastTrapStandTarget = null;
				return false;
			}
		}
		return true;
	}

	private bool TryGetSafeAdjacentStandPoint(Point target, Chara pc, out Point bestStand, out float bestDist)
	{
		bestStand = null;
		bestDist = float.MaxValue;
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				if (dx == 0 && dz == 0)
				{
					continue;
				}
				Point n = new Point(target.x + dx, target.z + dz);
				if (!n.IsValid || !n.IsInBounds || n.IsBlocked)
				{
					continue;
				}
				Cell nc = n.cell;
				if (nc == null || nc.outOfBounds || IsForbiddenDeepWater(nc, pc))
				{
					continue;
				}
				if (EffectiveAvoidTraps && HasTrapOnCell(nc, pc))
				{
					continue;
				}
				if (IsTileBlockedByNonReplaceableChara(pc, n))
				{
					continue;
				}
				if (!CanInteractBetweenPoints(n, target, allowBlockedTarget: target.IsBlocked))
				{
					continue;
				}
				if (!TryGetStandTilePathDistance(pc, n.x, n.z, allowPassThroughHostile: false, out var d))
				{
					continue;
				}
				if (d < bestDist)
				{
					bestDist = d;
					bestStand = n;
				}
			}
		}
		return bestStand != null;
	}

	/// <summary>按当前设置的战斗目标优先级（优先打近处 Nearest / 优先打血少 LowestHp）及【真实寻路路程】扫描最佳攻击目标。
	/// 彻底解决地牢中隔着一堵墙直线距离很近、实际要绕半张图才到得了而引发的盲目绕路！</summary>
	private Chara FindBestEnemy(Chara pc, float maxRadiusOverride = -1f, bool forceFullMap = false)
	{
		if (pc == null || EClass._map == null)
		{
			return null;
		}
		float now = Time.time;
		GetGatherScanBounds(out var minX, out var maxX, out var minZ, out var maxZ);
		bool enforceBounds = !IsInDungeonContext() && UseCustomSearchBounds && maxRadiusOverride <= 0f;

		// 先预热单源寻路距离场，后续所有候选怪物的可达性与真实步数均为 O(1) 查表
		EnsureSafeDistanceField(pc);

		Chara bestStrictEnemy = null;
		float bestStrictDist = float.MaxValue;
		int bestStrictHp = int.MaxValue;
		float bestStrictHpRatio = float.MaxValue;

		Chara bestPassEnemy = null;
		float bestPassDist = float.MaxValue;
		int bestPassHp = int.MaxValue;
		float bestPassHpRatio = float.MaxValue;

		foreach (Chara chara in EClass._map.charas)
		{
			if (chara == null || chara == pc || chara.isDead)
			{
				continue;
			}
			Point ePos = ((Card)chara).pos;
			if (ePos == null || !ePos.IsValid || !ePos.IsInBounds || ePos.cell == null || ePos.cell.outOfBounds)
			{
				continue;
			}
			if (enforceBounds && (ePos.x < minX || ePos.x > maxX || ePos.z < minZ || ePos.z > maxZ))
			{
				continue;
			}
			int uid = ((Card)chara).uid;
			if (_failedEnemies.TryGetValue(uid, out var expireTime))
			{
				if (now < expireTime)
				{
					continue;
				}
				_failedEnemies.Remove(uid);
			}

			// 直线距离作为快速下界粗筛（真实行走路程必定 >= 直线距离）
			float euclidDist = GetDistance(pc, chara);
			if (maxRadiusOverride > 0f && euclidDist > maxRadiusOverride + 0.5f)
			{
				continue;
			}
			if (!forceFullMap && maxRadiusOverride <= 0f && combatScanMode == CombatScanMode.Nearby && euclidDist > (float)CombatScanRadius + 0.5f)
			{
				continue;
			}
			if (euclidDist > 1.6f && IsForbiddenDeepWater(ePos.cell, pc))
			{
				continue;
			}

			bool isTarget = false;
			if (chara.IsHostile())
			{
				isTarget = true;
			}
			else if (AttackChicken)
			{
				// 保护己方阵营/宠物/家畜与城镇安全区，仅在野外/地牢等非城镇、非己方领地中攻击中立野生/家禽动物
				bool safeWildContext = !chara.IsPCFaction && !chara.IsPCFactionMinion && chara.hostility == Hostility.Neutral
					&& (EClass._zone == null || (!EClass._zone.IsTown && !EClass._zone.IsPCFaction));
				if (safeWildContext)
				{
					// 优先使用 Elin 原生动物判定 (race.IsAnimal || HasTag(CTAG.animal))，自动覆盖鸡/鸭/鹅/羊/猪/牛/兔/鹿/渡渡鸟/火鸡等全物种
					if (chara.IsAnimal)
					{
						isTarget = true;
					}
					else
					{
						string name = ((Card)chara).Name ?? "";
						if (name.Contains("鸡") || name.Contains("鸭") || name.Contains("山羊") || name.Contains("羊") || name.Contains("野兔") || name.Contains("兔") || name.Contains("猪") || name.Contains("鹅") || name.Contains("牛") || name.Contains("鹿") || name.Contains("渡渡鸟") || name.Contains("火鸡"))
						{
							isTarget = true;
						}
					}
				}
			}
			if (!isTarget)
			{
				continue;
			}

			int hp = ((Card)chara).hp;
			float hpRatio = ((((Card)chara).MaxHP > 0) ? ((float)hp / (float)((Card)chara).MaxHP) : 1f);
			float stickyBonus = ((_currentCombatTarget == chara) ? (-1.5f) : 0f);

			// 1) 检查是否无需穿过其他怪物即可直接到达攻击落脚格（或已在贴身直接攻击位）
			if (TryGetReachableAttackStandPoint(pc, chara, out _, out var strictPathDist, allowPassThroughHostile: false))
			{
				// 关键：近身自卫半径与周边索敌半径一律用【真实行走路程 strictPathDist】校验！
				// 隔墙怪即使直线只有 2 格，只要绕路超过 4.5 格，绝不会误触发近身自卫打断当前拾取或采集！
				if (maxRadiusOverride > 0f && strictPathDist > maxRadiusOverride)
				{
					continue;
				}
				if (!forceFullMap && maxRadiusOverride <= 0f && combatScanMode == CombatScanMode.Nearby && strictPathDist > (float)CombatScanRadius)
				{
					continue;
				}

				float effectiveDist = strictPathDist + stickyBonus;
				bool isBetter = false;
				if (bestStrictEnemy == null)
				{
					isBetter = true;
				}
				else if (combatTargetPriority == CombatTargetPriority.LowestHp)
				{
					if (hp != bestStrictHp)
					{
						isBetter = hp < bestStrictHp;
					}
					else if (Mathf.Abs(hpRatio - bestStrictHpRatio) > 0.001f)
					{
						isBetter = hpRatio < bestStrictHpRatio;
					}
					else
					{
						isBetter = effectiveDist < bestStrictDist;
					}
				}
				else
				{
					isBetter = effectiveDist < bestStrictDist;
				}

				if (isBetter)
				{
					bestStrictEnemy = chara;
					bestStrictDist = effectiveDist;
					bestStrictHp = hp;
					bestStrictHpRatio = hpRatio;
				}
				continue;
			}

			// 2) 窄道多怪兜底：若需要穿过前排挡路怪才能接触该怪
			if (TryGetReachableAttackStandPoint(pc, chara, out _, out var passPathDist, allowPassThroughHostile: true))
			{
				if (maxRadiusOverride > 0f && passPathDist > maxRadiusOverride)
				{
					continue;
				}
				if (!forceFullMap && maxRadiusOverride <= 0f && combatScanMode == CombatScanMode.Nearby && passPathDist > (float)CombatScanRadius)
				{
					continue;
				}

				float effectiveDist = passPathDist + stickyBonus;
				bool isBetter = false;
				if (bestPassEnemy == null)
				{
					isBetter = true;
				}
				else if (combatTargetPriority == CombatTargetPriority.LowestHp)
				{
					if (hp != bestPassHp)
					{
						isBetter = hp < bestPassHp;
					}
					else if (Mathf.Abs(hpRatio - bestPassHpRatio) > 0.001f)
					{
						isBetter = hpRatio < bestPassHpRatio;
					}
					else
					{
						isBetter = effectiveDist < bestPassDist;
					}
				}
				else
				{
					isBetter = effectiveDist < bestPassDist;
				}

				if (isBetter)
				{
					bestPassEnemy = chara;
					bestPassDist = effectiveDist;
					bestPassHp = hp;
					bestPassHpRatio = hpRatio;
				}
			}
		}

		return bestStrictEnemy ?? bestPassEnemy;
	}

	private Chara FindNearestEnemy(Chara pc)
	{
		return FindBestEnemy(pc, maxRadiusOverride: -1f, forceFullMap: false);
	}

	private bool DoCombat(Chara pc, Chara enemy)
	{
		if (CombatSlot >= 0 && (_lastEquippedSlot != CombatSlot || !ToolSwitcher.IsSlotActuallyEquipped(CombatSlot)))
		{
			ToolSwitcher.SwitchSlot(CombatSlot);
			_lastEquippedSlot = CombatSlot;
		}

		float dist = GetDistance(pc, enemy);
		int uid = ((Card)enemy).uid;
		int curX = ((Card)pc).pos.x;
		int curZ = ((Card)pc).pos.z;

		if (uid != _lastCombatTargetUid)
		{
			_lastCombatTargetUid = uid;
			_combatMoveRetryCount = 0;
			_lastCombatTargetDist = dist;
			_lastCombatStandTarget = null;
			_combatStuckTicks = 0;
			_lastCombatPosX = curX;
			_lastCombatPosZ = curZ;
		}

		bool canDirectAttack = CanDirectlyAttackEnemy(pc, enemy);

		// 已经处在与怪物相邻、无隔墙/斜角墙阻挡且有视线 Los 的位置：启动或保持 GoalAutoCombat
		if (canDirectAttack)
		{
			_combatMoveRetryCount = 0;
			_combatStuckTicks = 0;
			_lastCombatStandTarget = null;
			_lastCombatPosX = curX;
			_lastCombatPosZ = curZ;
			_enemyFailCounts.Remove(uid);

			ResetProgressAnchor(pc);
			if (IsAIRunning(pc) && pc.ai is GoalCombat)
			{
				return true;
			}

			if (!StartPCActImmediate(pc, (AIAct)new SafeGoalAutoCombat(this, enemy)) || !(pc.ai is GoalCombat))
			{
				BlacklistEnemyWithBackoff(uid);
				_currentCombatTarget = null;
				_lastCombatTargetUid = -1;
				return false;
			}
			return true;
		}

		// 尚未到达无隔墙可攻击位置：先寻找怪物同侧的无隔墙安全落脚格 standPos，使用 SafeAI_Goto(standPos, 0) 精确绕墙/绕陷阱走过去
		if (!TryGetReachableAttackStandPoint(pc, enemy, out var standPos, out var distToStand, allowPassThroughHostile: true) || standPos == null)
		{
			BlacklistEnemyWithBackoff(uid);
			_currentCombatTarget = null;
			_lastCombatTargetUid = -1;
			_combatMoveRetryCount = 0;
			_lastCombatStandTarget = null;
			return false;
		}
		bool posChanged = curX != _lastCombatPosX || curZ != _lastCombatPosZ;
		_lastCombatPosX = curX;
		_lastCombatPosZ = curZ;

		if (posChanged || distToStand < _lastCombatTargetDist - 0.3f)
		{
			_lastCombatTargetDist = distToStand;
			_combatStuckTicks = 0;
			_combatMoveRetryCount = 0;
		}
		else
		{
			_combatStuckTicks++;
		}

		// 如果连续多个 Tick 位置未动且未能接近落脚格，说明被封死地形卡住，拉黑该怪
		if (_combatStuckTicks >= 8 || _combatMoveRetryCount > 8)
		{
			Log("Combat target unreachable/stuck behind wall, blacklisting uid=" + uid);
			BlacklistEnemyWithBackoff(uid);
			_currentCombatTarget = null;
			_lastCombatTargetUid = -1;
			_combatMoveRetryCount = 0;
			_lastCombatStandTarget = null;
			_combatStuckTicks = 0;
			return false;
		}

		// 若当前正在走向的 _lastCombatStandTarget 依然是该怪物的合法无阻挡攻击落脚格，保持当前 SafeAI_Goto 直到走完全程，绝不中途反复重发 Goto 导致左右折返飘逸
		bool prevStandStillValid = _lastCombatStandTarget != null
			&& _lastCombatStandTarget.IsValid
			&& _lastCombatStandTarget.IsInBounds
			&& !_lastCombatStandTarget.IsBlocked
			&& ((Card)enemy).pos != null
			&& _lastCombatStandTarget.Distance(((Card)enemy).pos) == 1
			&& CanInteractBetweenPoints(_lastCombatStandTarget, ((Card)enemy).pos, allowBlockedTarget: false)
			&& !IsTileBlockedByNonReplaceableChara(pc, _lastCombatStandTarget);
		bool needIssueGoto = !IsRunningGoto(pc)
			|| _lastCombatStandTarget == null
			|| (!prevStandStillValid && !_lastCombatStandTarget.Equals(standPos) && (distToStand <= 3.5f || _lastCombatStandTarget.Distance(standPos) > 1));
		if (needIssueGoto)
		{
			if (!posChanged)
			{
				_combatMoveRetryCount++;
			}
			_lastCombatStandTarget = standPos.Copy();
			if (!StartSafeGoto(pc, standPos, 0, false))
			{
				if (CanDirectlyAttackEnemy(pc, enemy))
				{
					ResetProgressAnchor(pc);
					return StartPCActImmediate(pc, (AIAct)new SafeGoalAutoCombat(this, enemy)) && pc.ai is GoalCombat;
				}
				BlacklistEnemyWithBackoff(uid);
				_currentCombatTarget = null;
				_lastCombatTargetUid = -1;
				_combatMoveRetryCount = 0;
				_lastCombatStandTarget = null;
				return false;
			}
		}
		return true;
	}

	private void BlacklistEnemyWithBackoff(int uid)
	{
		_enemyFailCounts.TryGetValue(uid, out var fails);
		fails++;
		_enemyFailCounts[uid] = fails;
		float duration = ((fails >= 3) ? 60f : ((fails == 2) ? 15f : 6f));
		_failedEnemies[uid] = Time.time + duration;
	}

	private void DoKite(Chara pc, Chara enemy)
	{
		if (pc == null || enemy == null)
		{
			return;
		}
		if (CombatSlot >= 0 && (_lastEquippedSlot != CombatSlot || !ToolSwitcher.IsSlotActuallyEquipped(CombatSlot)))
		{
			ToolSwitcher.SwitchSlot(CombatSlot);
			_lastEquippedSlot = CombatSlot;
		}
		GetGatherScanBounds(out var minX, out var maxX, out var minZ, out var maxZ);
		Point pcPos = ((Card)pc).pos;
		Point ePos = ((Card)enemy).pos;
		if (pcPos == null || ePos == null)
		{
			return;
		}
		float curEnemyDist = GetDistance(pcPos.x, pcPos.z, ePos.x, ePos.z);
		if (IsRunningGoto(pc) && pc.ai is AI_Goto curGoto)
		{
			Point curDest = curGoto.GetDestination();
			if (curDest != null && curDest.IsValid && !curDest.Equals(pcPos) && GetDistance(curDest.x, curDest.z, ePos.x, ePos.z) > curEnemyDist + 0.5f)
			{
				ResetProgressAnchor(pc);
				return;
			}
		}
		Point bestKitePt = null;
		float bestScore = -9999f;

		// 在周围 1..4 格可行动边界内搜索安全可达且能拉开与敌人距离的风筝落脚点（解决在地图边界/墙角处单点 Clamp 原地罚站导致 10 秒停机的问题）
		EnsureSafeDistanceField(pc);
		for (int dx = -4; dx <= 4; dx++)
		{
			for (int dz = -4; dz <= 4; dz++)
			{
				if (dx == 0 && dz == 0)
				{
					continue;
				}
				int cx = pcPos.x + dx;
				int cz = pcPos.z + dz;
				if (cx < minX || cx > maxX || cz < minZ || cz > maxZ)
				{
					continue;
				}
				Point cand = new Point(cx, cz);
				if (!cand.IsValid || !cand.IsInBounds || cand.IsBlocked)
				{
					continue;
				}
				Cell cell = cand.cell;
				if (cell == null || cell.outOfBounds || IsForbiddenDeepWater(cell, pc) || (EffectiveAvoidTraps && HasTrapOnCell(cell, pc)))
				{
					continue;
				}
				if (IsTileBlockedByNonReplaceableChara(pc, cand))
				{
					continue;
				}
				float distToEnemy = GetDistance(cx, cz, ePos.x, ePos.z);
				if (distToEnemy <= curEnemyDist + 0.1f)
				{
					continue;
				}
				if (!TryGetStandTilePathDistance(pc, cx, cz, allowPassThroughHostile: false, out var walkDist) || walkDist > 6.5f)
				{
					continue;
				}
				float score = distToEnemy * 2.5f - walkDist * 0.5f;
				if (score > bestScore)
				{
					bestScore = score;
					bestKitePt = cand;
				}
			}
		}

		if (bestKitePt != null && StartSafeGoto(pc, bestKitePt, 0, false))
		{
			ResetProgressAnchor(pc);
			return;
		}

		// 若已在地图边界死角无处可退，立即反击自卫，绝不原地发呆等死或触发卡死停机
		if (DoCombat(pc, enemy))
		{
			CurrentState = State.Combat;
			ResetProgressAnchor(pc);
		}
	}

	private void DoRetreat(Chara pc)
	{
		if (pc == null)
		{
			return;
		}
		if (!IsRunningGoto(pc) || _retreatTarget == null)
		{
			_retreatTarget = FindNearestEdgePoint(pc);
			if (_retreatTarget != null && !((Card)pc).pos.Equals(_retreatTarget))
			{
				if (StartSafeGoto(pc, _retreatTarget, 0, false))
				{
					ResetProgressAnchor(pc);
					return;
				}
			}
		}
		// 若已退到地图边缘格且身边仍有贴身怪追打，立即原地自卫反击并保持重置卡死计时
		ResetProgressAnchor(pc);
		Chara nearEnemy = FindBestEnemy(pc, NearThreatRadius, forceFullMap: false);
		if (nearEnemy != null && CanDirectlyAttackEnemy(pc, nearEnemy))
		{
			_currentCombatTarget = nearEnemy;
			DoCombat(pc, nearEnemy);
		}
	}

	private Point FindNearestEdgePoint(Chara pc)
	{
		GetGatherScanBounds(out var minX, out var maxX, out var minZ, out var maxZ);
		Point best = null;
		float bestDist = float.MaxValue;
		for (int x = minX; x <= maxX; x++)
		{
			TryEdgePoint(pc, x, minZ, ref best, ref bestDist);
			TryEdgePoint(pc, x, maxZ, ref best, ref bestDist);
		}
		for (int z = minZ; z <= maxZ; z++)
		{
			TryEdgePoint(pc, minX, z, ref best, ref bestDist);
			TryEdgePoint(pc, maxX, z, ref best, ref bestDist);
		}
		return best;
	}

	private void TryEdgePoint(Chara pc, int x, int z, ref Point best, ref float bestDist)
	{
		Point val = new Point(x, z);
		if (val.IsValid && val.IsInBounds && !val.IsBlocked && !IsForbiddenDeepWater(val.cell, pc) && !(EffectiveAvoidTraps && HasTrapOnCell(val.cell, pc)))
		{
			if (TryGetStandTilePathDistance(pc, x, z, allowPassThroughHostile: true, out var distance) && distance < bestDist)
			{
				bestDist = distance;
				best = val;
			}
		}
	}

	/// <summary>判断是否直接使用当前地图真实的玩家可行动边界 (EClass._map.bounds)：
	/// - 地牢环境始终使用全可行动边界；
	/// - 野外/家园环境下，只要未勾选"手动指定子区域范围 (UseCustomSearchBounds)"，也直接读取 EClass._map.bounds！</summary>
	private bool ShouldUseMapPlayableBoundsDirectly()
	{
		if (EClass._zone == null || EClass._map == null)
		{
			return false;
		}
		if (((Spatial)EClass._zone).IsRegion)
		{
			return false;
		}
		if (IsInDungeonContext() || IsDungeonNavigating)
		{
			return true;
		}
		return !UseCustomSearchBounds;
	}

	private void GetGatherScanBounds(out int minX, out int maxX, out int minZ, out int maxZ)
	{
		if (!TryGetMapPlayableBounds(out var bMinX, out var bMaxX, out var bMinZ, out var bMaxZ))
		{
			minX = 0;
			maxX = -1;
			minZ = 0;
			maxZ = -1;
			return;
		}
		if (ShouldUseMapPlayableBoundsDirectly())
		{
			minX = bMinX;
			maxX = bMaxX;
			minZ = bMinZ;
			maxZ = bMaxZ;
		}
		else
		{
			minX = Mathf.Clamp(SearchMinX, bMinX, bMaxX);
			maxX = Mathf.Clamp(SearchMaxX, bMinX, bMaxX);
			minZ = Mathf.Clamp(SearchMinZ, bMinZ, bMaxZ);
			maxZ = Mathf.Clamp(SearchMaxZ, bMinZ, bMaxZ);
		}
	}

	/// <summary>一键将当前地图真实的 EClass._map.bounds 填入自定义框参数。</summary>
	public void SyncCustomBoundsToCurrentMap()
	{
		if (TryGetMapPlayableBounds(out var bMinX, out var bMaxX, out var bMinZ, out var bMaxZ))
		{
			SearchMinX = bMinX;
			SearchMaxX = bMaxX;
			SearchMinZ = bMinZ;
			SearchMaxZ = bMaxZ;
			SaveConfig();
		}
	}

	/// <summary>当目标格本身不可通行或位于禁入深水区时，检查周围8格是否至少有一格安全可落脚且无隔墙阻挡。</summary>
	private bool HasWalkableNeighbor(Point p, Chara pc)
	{
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				if (dx == 0 && dz == 0)
				{
					continue;
				}
				Point n = new Point(p.x + dx, p.z + dz);
				if (!n.IsValid || !n.IsInBounds || n.IsBlocked)
				{
					continue;
				}
				Cell nc = n.cell;
				if (nc == null || nc.outOfBounds || IsForbiddenDeepWater(nc, pc))
				{
					continue;
				}
				if (EffectiveAvoidTraps && HasVisibleTrapOnCell(nc, pc))
				{
					continue;
				}
				if (!CanInteractBetweenPoints(n, p, allowBlockedTarget: p.IsBlocked))
				{
					continue;
				}
				return true;
			}
		}
		return false;
	}

	private int ResolveHarvestElementByRawKey(string rawKey)
	{
		if (string.IsNullOrEmpty(rawKey))
		{
			return -1;
		}
		if (rawKey == "digging")
		{
			return 230;
		}
		if (rawKey == "gathering")
		{
			return 250;
		}
		if (EClass.sources?.elements?.alias != null && EClass.sources.elements.alias.ContainsKey(rawKey))
		{
			return EClass.sources.elements.alias[rawKey].id;
		}
		return -1;
	}

	/// <summary>解析原版 TaskHarvest.TryGetAct 中 reqHarvest[0] 对应的技能元素 ID：
	/// 250 = gathering(徒手/通用采集，如蜘蛛网、藤蔓、草药)，225 = chopping/logging(伐木)，220 = mining(采掘)，230 = digging(挖掘)。</summary>
	private int ResolveHarvestElement(Point p, string rawKey, bool checkCellGrowth)
	{
		if (string.IsNullOrEmpty(rawKey))
		{
			return -1;
		}
		if (rawKey == "digging")
		{
			return 230;
		}
		if (rawKey == "gathering")
		{
			return 250;
		}
		if (checkCellGrowth && p.cell != null && p.cell.CanHarvest())
		{
			return 250;
		}
		if (EClass.sources?.elements?.alias != null && EClass.sources.elements.alias.ContainsKey(rawKey))
		{
			return EClass.sources.elements.alias[rawKey].id;
		}
		return -1;
	}

	/// <summary>检查格子上的 Obj（含地面树木/草药、骸骨残骸 mound、盐矿/矿石、晶簇、藤蔓、以及附着在墙体上的藤蔓/矿脉/铁皮）是否可采集。</summary>
	private bool TryGetObjHarvestInfo(Point p, out int reqEle)
	{
		reqEle = -1;
		if (!p.HasObj || p.sourceObj == null)
		{
			return false;
		}
		string[] req = p.sourceObj.reqHarvest;
		if (req == null || req.Length == 0 || string.IsNullOrEmpty(req[0]))
		{
			return false;
		}
		reqEle = ResolveHarvestElement(p, req[0], checkCellGrowth: true);
		return reqEle > 0;
	}

	/// <summary>检查格子上的 Thing（如蜘蛛网 TraitCobWeb : TraitHarvest、地牢残骸等）是否可采集。
	/// 完全对齐原版 TaskHarvest.TryGetAct 第 179-193 行的判定，并支持同名/同类跨图通用匹配。</summary>
	private bool TryGetThingHarvestInfo(Point p, out int reqEle, out Thing harvestThing, bool onlyTargeted = false)
	{
		reqEle = -1;
		harvestThing = null;
		if (!p.HasThing)
		{
			return false;
		}
		List<Thing> things = p.Things;
		if (things == null)
		{
			return false;
		}
		for (int num = things.Count - 1; num >= 0; num--)
		{
			Thing t = things[num];
			if (t == null || ((Card)t).isDestroyed || ((Card)t).isHidden || ((Card)t).isMasked || t.trait == null)
			{
				continue;
			}
			string reqHarvest = t.trait.ReqHarvest;
			if (string.IsNullOrEmpty(reqHarvest))
			{
				continue;
			}
			string[] raw = reqHarvest.Split(',');
			if (raw == null || raw.Length == 0 || string.IsNullOrEmpty(raw[0]))
			{
				continue;
			}
			int ele = ResolveHarvestElement(p, raw[0], checkCellGrowth: false);
			if (ele <= 0)
			{
				continue;
			}
			if (ele == 250 && !t.trait.CanHarvest)
			{
				continue;
			}
			if (onlyTargeted)
			{
				bool matched = ((ele == 220 || ele == 230) ? (IsMineThingTargetMatched(t) || IsGatherThingTargetMatched(t)) : IsGatherThingTargetMatched(t));
				if (!matched)
				{
					continue;
				}
			}
			reqEle = ele;
			harvestThing = t;
			return true;
		}
		return false;
	}

	/// <summary>严格校验指定工具是否具备目标采集所需的技能元素（解决原版 TaskHarvest.TryGetAct 第 139 行把斧头 225 放行用于挖矿 220 导致拿着斧头去挖铜矿的问题）。</summary>
	private bool DoesToolMatchHarvestRequirement(Thing tool, int reqEle, bool isSeedTarget)
	{
		if (isSeedTarget)
		{
			return tool != null && (tool.trait is TraitToolSickle || tool.category.IsChildOf("scythe"));
		}
		if (reqEle == 230)
		{
			return tool != null && ((Card)tool).HasElement(230, false);
		}
		if (reqEle == 220)
		{
			return tool != null && (((Card)tool).HasElement(220, false) || tool.trait is TraitToolHammer);
		}
		if (reqEle == 225)
		{
			return tool != null && (((Card)tool).HasElement(225, false) || ((Card)tool).HasElement(220, false));
		}
		if (reqEle == 250)
		{
			if (tool == null)
			{
				return true;
			}
			if (tool.trait is TraitToolSickle || tool.category.IsChildOf("scythe") || tool.trait is TraitToolBrush || tool.trait is TraitToolShears || tool.trait is TraitToolWaterCan || tool.trait is TraitToolMusic || tool.trait is TraitAbility)
			{
				return false;
			}
			return tool.trait == null || tool.trait.CanHarvest;
		}
		return true;
	}

	private bool DoesSlotHoldMatchingTool(int slot, int reqEle, bool isSeedTarget)
	{
		if (slot < 0)
		{
			return false;
		}
		Thing slotThing = ToolSwitcher.GetSlotThing(slot);
		if (slotThing == null)
		{
			return false;
		}
		return DoesToolMatchHarvestRequirement(slotThing, reqEle, isSeedTarget);
	}

	private int GetGatherSlotForElement(int reqEle)
	{
		if (reqEle == 220 || reqEle == 230)
		{
			if (MiningSlot >= 0)
			{
				return MiningSlot;
			}
			return (ChoppingSlot >= 0 && DoesSlotHoldMatchingTool(ChoppingSlot, reqEle, isSeedTarget: false)) ? ChoppingSlot : (-1);
		}
		int slot = (ChoppingSlot >= 0) ? ChoppingSlot : MiningSlot;
		if (slot < 0 && reqEle == 250)
		{
			// 250 (gathering) 在原版 BaseTaskHarvest 中 isToolRequired = false，未设置工具槽时也允许空手或持武器直接采集
			return (CombatSlot >= 0) ? CombatSlot : SlotHandOrCurrent;
		}
		return slot;
	}

	/// <summary>对齐原版 BaseTaskHarvest.GetToolEfficiency(SourceMaterial.Row mat) 的工具效率权重：
	/// - 若目标是采掘/挖掘 (reqEle == 220/230)，严格要求使用具备采掘能力的槽位，绝不拿纯斧头去挖铜矿/矿石；
	/// - 若是通用采集 (250) 或伐木 (225)，按材质匹配最优槽位。</summary>
	private int GetGatherSlotForMaterialAndElement(SourceMaterial.Row mat, int reqEle)
	{
		if (reqEle == 220 || reqEle == 230)
		{
			if (MiningSlot >= 0)
			{
				return MiningSlot;
			}
			return (ChoppingSlot >= 0 && DoesSlotHoldMatchingTool(ChoppingSlot, reqEle, isSeedTarget: false)) ? ChoppingSlot : (-1);
		}
		string cat = mat?.category ?? "";
		int slot;
		if (cat == "bone" || cat == "ore" || cat == "crystal" || cat == "rock" || cat == "gem")
		{
			slot = (MiningSlot >= 0) ? MiningSlot : ChoppingSlot;
		}
		else if (cat == "wood" || cat == "grass" || cat == "fiber" || cat == "organic" || cat == "skin")
		{
			slot = (ChoppingSlot >= 0) ? ChoppingSlot : MiningSlot;
		}
		else
		{
			slot = GetGatherSlotForElement(reqEle);
		}
		if (slot < 0 && reqEle == 250)
		{
			return (CombatSlot >= 0) ? CombatSlot : SlotHandOrCurrent;
		}
		return slot;
	}

	/// <summary>野外环境下，如果地图（非玩家基地/非城镇）存在纯岩盐块（HasBlock 且非普通土墙石墙），允许野外挖矿直接开采。</summary>
	private bool CanMineSaltOrMineralBlockInField(Point p, out int neededSlot, bool ignoreFieldMineToggle = false)
	{
		neededSlot = -1;
		if ((!FieldMine && !ignoreFieldMineToggle) || MiningSlot < 0)
		{
			return false;
		}
		if (EClass._zone == null || EClass._zone.IsPCFaction || EClass._zone.IsTown)
		{
			return false;
		}
		if (!p.HasBlock || p.matBlock == null || p.sourceBlock == null)
		{
			return false;
		}
		if (p.sourceBlock.tileType != null && p.sourceBlock.tileType.Invisible)
		{
			return false;
		}
		string alias = (p.matBlock.alias ?? "").ToLowerInvariant();
		string name = p.matBlock.name ?? "";
		string nameJp = p.matBlock.name_JP ?? "";
		if (alias == "salt" || alias.Contains("salt") || name.IndexOf("salt", StringComparison.OrdinalIgnoreCase) >= 0 || nameJp.Contains("塩"))
		{
			neededSlot = MiningSlot;
			return true;
		}
		return false;
	}

	/// <summary>地牢环境下，按地牢敲墙开关与原版 TaskMine.CanMine / BaseTaskHarvest.GetToolEfficiency 规则判定当前墙体是否可敲及对应工具槽位。</summary>
	private bool CanMineBlockInDungeonMode(Point p, out int neededSlot)
	{
		neededSlot = -1;
		if (!IsInDungeonContext() || !DungeonMineWalls)
		{
			return false;
		}
		if (DungeonMineWallLimit > 0 && _dungeonMinedWallCount >= DungeonMineWallLimit)
		{
			return false;
		}
		if (!p.HasBlock || p.matBlock == null || p.matBlock.id == 0 || p.sourceBlock == null || p.cell.hasDoor)
		{
			return false;
		}
		if (p.sourceBlock.tileType != null && (p.sourceBlock.tileType.Invisible || !p.sourceBlock.tileType.IsFullBlock))
		{
			return false;
		}
		if (p.HasObj && p.sourceObj != null && p.sourceObj.tileType != null && p.sourceObj.tileType.IsBlockMount)
		{
			return false;
		}

		string cat = p.matBlock.category ?? "";
		bool isWoodOrGrass = cat == "wood" || cat == "grass";
		bool matched = isWoodOrGrass || cat == "soil" || cat == "rock" || cat == "ore" || cat == "crystal" || cat == "gem" || cat == "bone";
		if (!matched)
		{
			return false;
		}

		if (isWoodOrGrass)
		{
			neededSlot = ((ChoppingSlot >= 0) ? ChoppingSlot : MiningSlot);
		}
		else
		{
			neededSlot = ((MiningSlot >= 0) ? MiningSlot : ChoppingSlot);
		}
		return neededSlot >= 0;
	}

	/// <summary>严格对齐原版 TaskMine.CanMine 与 SourceMaterial.Row.category 的临时挖墙模块判定（独立模块：只要勾选了任意临时挖墙项即生效，不再受当前 UI 在哪个分页影响）。</summary>
	private bool CanMineBlockInCurrentMode(Point p, out int neededSlot)
	{
		neededSlot = -1;
		if (!HasAnyTempWallEnabled())
		{
			return false;
		}
		if (!p.HasBlock || p.matBlock == null || p.sourceBlock == null || p.cell.hasDoor)
		{
			return false;
		}
		if (p.sourceBlock.tileType != null && p.sourceBlock.tileType.Invisible)
		{
			return false;
		}

		string cat = p.matBlock.category ?? "";
		string alias = (p.matBlock.alias ?? "").ToLowerInvariant();
		string matName = p.matBlock.name ?? "";
		string matNameJp = p.matBlock.name_JP ?? "";

		bool isWoodOrGrass = cat == "wood" || cat == "grass";
		bool isSoilOrSand = cat == "soil" || alias.Contains("sand") || alias.Contains("soil") || alias.Contains("mud") || alias.Contains("clay") || matName.IndexOf("sand", StringComparison.OrdinalIgnoreCase) >= 0 || matNameJp.Contains("砂") || matNameJp.Contains("土") || matNameJp.Contains("泥");
		bool matched = false;

		if (TempMineAllWalls)
		{
			matched = true;
		}
		else
		{
			if (TempMineSoil && isSoilOrSand)
			{
				matched = true;
			}
			if (TempMineRock && (cat == "rock" || alias.Contains("sandstone")))
			{
				matched = true;
			}
			if (TempMineOre && cat == "ore")
			{
				matched = true;
			}
			if (TempMineWoodGrass && isWoodOrGrass)
			{
				matched = true;
			}
		}

		if (!matched)
		{
			return false;
		}

		if (isWoodOrGrass)
		{
			neededSlot = ((ChoppingSlot >= 0) ? ChoppingSlot : MiningSlot);
		}
		else
		{
			neededSlot = ((MiningSlot >= 0) ? MiningSlot : ChoppingSlot);
		}
		return neededSlot >= 0;
	}

	/// <summary>评估单个坐标格在"专项收集模式（TargetedGatherEnabled == true）"下的优先级与所需工具槽位。
	/// 采用同名/同类跨地图通用匹配，且不再受植物生长阶段引起的 objEle(225/250) 变动限制。
	/// 严格遵循优先级：1=种子 -> 2=采集(树木/蘑菇/蜘蛛网/花草/采集物) -> 3=挖矿(矿石/临时墙体)。</summary>
	private bool TryEvaluatePointForTargetedGather(Point p, out int priorityTier, out float scoreOffset, out int neededSlot, out bool isWallTarget, out bool isSeedTarget, out Thing specificThing)
	{
		priorityTier = 99;
		scoreOffset = 0f;
		neededSlot = -1;
		isWallTarget = false;
		isSeedTarget = false;
		specificThing = null;

		Cell cell = p.cell;
		if (cell == null)
		{
			return false;
		}

		// 优先级 1：种子（成熟可收种植物）
		if (SickleSlot >= 0 && p.HasObj && p.sourceObj != null && p.sourceObj.id != 0 && cell.CanReapSeed())
		{
			if (IsSeedTargetMatched(p.sourceObj))
			{
				priorityTier = 1;
				scoreOffset = 0f;
				neededSlot = SickleSlot;
				isWallTarget = false;
				isSeedTarget = true;
				return true;
			}
		}

		// 优先级 2：采集（树木、蘑菇 GrowSystemKinoko、草药、花、浆果、杂草、蜘蛛网、残骸 mound 等）
		if (p.HasObj && TryGetObjHarvestInfo(p, out var objEle) && p.sourceObj != null && p.sourceObj.id != 0)
		{
			SourceObj.Row sObj = p.sourceObj;
			SourceMaterial.Row matObj = cell.matObj;

			if (IsTreeObjRow(sObj))
			{
				if (IsTreeTargetMatched(sObj, matObj))
				{
					int slot = GetGatherSlotForMaterialAndElement(matObj, objEle);
					if (slot >= 0 || slot == SlotHandOrCurrent)
					{
						priorityTier = 2;
						scoreOffset = 0f;
						neededSlot = slot;
						return true;
					}
				}
			}
			else if (!IsMineObjRow(sObj))
			{
				// 保护玩家领地内种植的农作物/浆果：在玩家家里只摘成熟果实(cell.CanHarvest())，不把已摘空的果树/作物铲掉；野外和地牢则全部直接采集
				bool skipHomePlantedCrop = EClass._zone != null && EClass._zone.IsPCFactionOrTent
					&& sObj.growth != null && sObj.growth.harvestTile != 0 && sObj.growth.HarvestStage >= 0
					&& !cell.CanHarvest();
				if (!skipHomePlantedCrop && IsGatherObjTargetMatched(sObj, matObj))
				{
					int slot = GetGatherSlotForMaterialAndElement(matObj, objEle);
					if (slot >= 0 || slot == SlotHandOrCurrent)
					{
						priorityTier = 2;
						scoreOffset = 0f;
						neededSlot = slot;
						return true;
					}
				}
			}
		}

		if (p.HasThing && TryGetThingHarvestInfo(p, out var thingEle, out var matchedThing, onlyTargeted: true))
		{
			if (thingEle == 225 || thingEle == 250)
			{
				int slot = GetGatherSlotForMaterialAndElement(matchedThing?.material, thingEle);
				if (slot >= 0 || slot == SlotHandOrCurrent)
				{
					priorityTier = 2;
					scoreOffset = 0f;
					neededSlot = slot;
					specificThing = matchedThing;
					return true;
				}
			}
		}

		// 优先级 3：挖矿（采掘类 Obj 220/230、采掘类 Thing、盐矿/指定墙体、以及并行开启的临时挖墙模块）
		if (p.HasObj && TryGetObjHarvestInfo(p, out var mObjEle) && (mObjEle == 220 || mObjEle == 230 || IsMineObjRow(p.sourceObj)) && p.sourceObj != null && p.sourceObj.id != 0)
		{
			SourceMaterial.Row matObj = cell.matObj;
			if (IsMineObjTargetMatched(p.sourceObj, matObj))
			{
				int slot = GetGatherSlotForMaterialAndElement(matObj, mObjEle);
				if (slot >= 0)
				{
					priorityTier = 3;
					scoreOffset = 0f;
					neededSlot = slot;
					return true;
				}
			}
		}

		if (p.HasThing && TryGetThingHarvestInfo(p, out var mThingEle, out var matchedMineThing, onlyTargeted: true) && (mThingEle == 220 || mThingEle == 230))
		{
			int slot = GetGatherSlotForMaterialAndElement(matchedMineThing?.material, mThingEle);
			if (slot >= 0)
			{
				priorityTier = 3;
				scoreOffset = 0f;
				neededSlot = slot;
				specificThing = matchedMineThing;
				return true;
			}
		}

		if (p.HasBlock && p.matBlock != null && p.matBlock.id != 0)
		{
			if (IsMineMaterialMatched(p.matBlock) && CanMineSaltOrMineralBlockInField(p, out var saltSlot, ignoreFieldMineToggle: true))
			{
				priorityTier = 3;
				scoreOffset = 0.5f;
				neededSlot = saltSlot;
				isWallTarget = true;
				return true;
			}
			if (!string.IsNullOrEmpty(p.matBlock.alias) && TargetedGatherKeys.Contains("wall_mat:" + p.matBlock.alias) && !cell.hasDoor && (MiningSlot >= 0 || ChoppingSlot >= 0))
			{
				bool isWood = p.matBlock.category == "wood" || p.matBlock.category == "grass";
				int wSlot = (isWood ? ((ChoppingSlot >= 0) ? ChoppingSlot : MiningSlot) : ((MiningSlot >= 0) ? MiningSlot : ChoppingSlot));
				if (wSlot >= 0)
				{
					// 普通墙体排在所有地面采集物（1..3）之后，设为优先级 4
					priorityTier = 4;
					scoreOffset = 2.0f;
					neededSlot = wSlot;
					isWallTarget = true;
					return true;
				}
			}
		}

		// 临时挖墙模块作为独立附加模块，排在普通地面采集物之后（优先级 4）
		if (CanMineBlockInCurrentMode(p, out var tempWallSlot))
		{
			priorityTier = 4;
			scoreOffset = 2.0f;
			neededSlot = tempWallSlot;
			isWallTarget = true;
			return true;
		}

		return false;
	}

	/// <summary>评估单个坐标格在"默认全部采集（TargetedGatherEnabled == false）"下的优先级与所需工具槽位。
	/// 自动按当前地图环境（inDungeon）采用地牢或野外设置，并叠加临时挖墙模块，严格遵循：1=种子 -> 2=采集 -> 3=挖矿。</summary>
	private bool TryEvaluatePointForDefaultGather(Point p, bool inDungeon, out int priorityTier, out float scoreOffset, out int neededSlot, out bool isWallTarget, out bool isSeedTarget, out Thing specificThing)
	{
		priorityTier = 99;
		scoreOffset = 0f;
		neededSlot = -1;
		isWallTarget = false;
		isSeedTarget = false;
		specificThing = null;

		Cell cell = p.cell;
		if (cell == null)
		{
			return false;
		}

		bool allowCrops = (inDungeon ? DungeonCrops : FieldCrops);
		bool allowChop = (inDungeon ? DungeonChop : FieldChop);
		bool allowMine = (inDungeon ? DungeonMineObjs : FieldMine);

		// ---------- 优先级 1：种子（CanReapSeed） ----------
		if (allowCrops && SickleSlot >= 0 && cell.CanReapSeed())
		{
			priorityTier = 1;
			scoreOffset = 0f;
			neededSlot = SickleSlot;
			isWallTarget = false;
			isSeedTarget = true;
			return true;
		}

		// ---------- 优先级 2：采集（树木 225、草药/藤蔓/蜘蛛网/蘑菇/残骸 250） ----------
		if (TryGetObjHarvestInfo(p, out var objEle))
		{
			SourceMaterial.Row matObj = cell.matObj;
			string matCat = matObj?.category ?? "";
			bool isRemains = (p.sourceObj?.alias == "mound") || matCat == "bone";
			bool isMineralMat = matCat == "ore" || matCat == "crystal" || matCat == "rock" || matCat == "gem";

			if (inDungeon && isRemains && (allowChop || allowMine))
			{
				int slot = GetGatherSlotForMaterialAndElement(matObj, objEle);
				if (slot >= 0 || slot == SlotHandOrCurrent)
				{
					priorityTier = 2;
					scoreOffset = 0f;
					neededSlot = slot;
					return true;
				}
			}
			if (objEle == 225 && allowChop)
			{
				int slot = GetGatherSlotForMaterialAndElement(matObj, objEle);
				if (slot >= 0 || slot == SlotHandOrCurrent)
				{
					priorityTier = 2;
					scoreOffset = 0f;
					neededSlot = slot;
					return true;
				}
			}
			if (objEle == 250 && (allowChop || (allowMine && isMineralMat)))
			{
				int slot = GetGatherSlotForMaterialAndElement(matObj, 250);
				if (slot >= 0 || slot == SlotHandOrCurrent)
				{
					// 若是纯矿石/水晶材质且仅靠挖矿开关命中，归入挖矿优先级；否则归入采集优先级
					priorityTier = ((!allowChop && isMineralMat) ? 3 : 2);
					scoreOffset = 0f;
					neededSlot = slot;
					return true;
				}
			}
		}

		if (TryGetThingHarvestInfo(p, out var thingEle, out var harvestThing, onlyTargeted: false))
		{
			if ((thingEle == 225 || thingEle == 250) && (allowChop || (!inDungeon && allowMine)))
			{
				int slot = GetGatherSlotForMaterialAndElement(harvestThing?.material, thingEle);
				if (slot >= 0 || slot == SlotHandOrCurrent)
				{
					priorityTier = 2;
					scoreOffset = 0f;
					neededSlot = slot;
					specificThing = harvestThing;
					return true;
				}
			}
		}

		// ---------- 优先级 3：挖矿（矿石/晶簇/岩石 220/230、盐矿块、临时挖墙模块、地牢自动敲墙） ----------
		if (TryGetObjHarvestInfo(p, out var mObjEle) && (mObjEle == 220 || mObjEle == 230) && allowMine)
		{
			int slot = GetGatherSlotForMaterialAndElement(cell.matObj, mObjEle);
			if (slot >= 0)
			{
				priorityTier = 3;
				scoreOffset = 0f;
				neededSlot = slot;
				return true;
			}
		}

		if (TryGetThingHarvestInfo(p, out var mThingEle, out var mHarvestThing, onlyTargeted: false) && (mThingEle == 220 || mThingEle == 230) && allowMine)
		{
			int slot = GetGatherSlotForMaterialAndElement(mHarvestThing?.material, mThingEle);
			if (slot >= 0)
			{
				priorityTier = 3;
				scoreOffset = 0f;
				neededSlot = slot;
				specificThing = mHarvestThing;
				return true;
			}
		}

		if (!inDungeon && CanMineSaltOrMineralBlockInField(p, out var saltWallSlot))
		{
			priorityTier = 3;
			scoreOffset = 0.5f;
			neededSlot = saltWallSlot;
			isWallTarget = true;
			return true;
		}

		// 临时挖墙模块：排在普通地面采集物（1..3）之后，设为优先级 4
		if (CanMineBlockInCurrentMode(p, out var tempWallSlot))
		{
			priorityTier = 4;
			scoreOffset = 2.0f;
			neededSlot = tempWallSlot;
			isWallTarget = true;
			return true;
		}

		// 地牢自动敲普通墙：排在普通地面采集物（1..3）之后，设为优先级 4
		if (inDungeon && CanMineBlockInDungeonMode(p, out var dWallSlot))
		{
			priorityTier = 4;
			scoreOffset = 2.5f;
			neededSlot = dWallSlot;
			isWallTarget = true;
			return true;
		}

		return false;
	}

	private Point FindGatherTarget(Chara pc, out int neededSlot, out bool isWallTarget, out bool isSeedTarget, out Thing specificThing)
	{
		neededSlot = -1;
		isWallTarget = false;
		isSeedTarget = false;
		specificThing = null;

		UpgradeLegacyTargetedGatherKeysIfReady();
		bool inDungeon = IsInDungeonContext();
		bool hasTempWalls = HasAnyTempWallEnabled() && (MiningSlot >= 0 || ChoppingSlot >= 0);

		if (TargetedGatherEnabled)
		{
			if (TargetedGatherKeys.Count == 0 && !hasTempWalls)
			{
				return null;
			}
		}
		else if (inDungeon)
		{
			bool canDChop = DungeonChop;
			bool canDMine = DungeonMineObjs && (MiningSlot >= 0 || ChoppingSlot >= 0);
			bool canDCrops = DungeonCrops && SickleSlot >= 0;
			bool canDWalls = DungeonMineWalls && (MiningSlot >= 0 || ChoppingSlot >= 0);
			if (!canDChop && !canDMine && !canDCrops && !canDWalls && !hasTempWalls)
			{
				return null;
			}
		}
		else
		{
			bool canChop = FieldChop;
			bool canMine = FieldMine && (MiningSlot >= 0 || ChoppingSlot >= 0);
			bool canCrops = FieldCrops && SickleSlot >= 0;
			if (!canChop && !canMine && !canCrops && !hasTempWalls)
			{
				return null;
			}
		}

		int bestTier = 99;
		float bestScore = float.MaxValue;
		Point bestPoint = null;
		int bestSlot = -1;
		bool bestIsWall = false;
		bool bestIsSeed = false;
		Thing bestSpecificThing = null;
		float now = Time.time;

		GetGatherScanBounds(out var minX, out var maxX, out var minZ, out var maxZ);
		// 先预热单源寻路距离场，使全图所有采集点的真实绕墙路程查询为 O(1)
		EnsureSafeDistanceField(pc);
		int pcX = ((Card)pc).pos.x;
		int pcZ = ((Card)pc).pos.z;
		Cell prevGrowCell = GrowSystem.cell;
		try
		{
			for (int i = minX; i <= maxX; i++)
			{
				for (int j = minZ; j <= maxZ; j++)
				{
					string item = i + "," + j;
					if (_failedTargets.Contains(item))
					{
						continue;
					}
					if (_unreachableGatherTargets.TryGetValue(item, out var expireTime))
					{
						if (now < expireTime)
						{
							continue;
						}
						_unreachableGatherTargets.Remove(item);
					}
					Point val3 = new Point(i, j);
					if (!val3.IsValid || !val3.IsInBounds)
					{
						continue;
					}
					Cell cell = val3.cell;
					if (cell == null || cell.outOfBounds)
					{
						continue;
					}
					if (EffectiveAvoidTraps && HasVisibleTrapOnCell(cell, pc))
					{
						continue;
					}
					if ((val3.IsBlocked || IsForbiddenDeepWater(cell, pc)) && !HasWalkableNeighbor(val3, pc))
					{
						continue;
					}

					int candTier;
					float candOffset;
					int candSlot;
					bool candIsWall;
					bool candIsSeed;
					Thing candThing;

					bool matched = (TargetedGatherEnabled
						? TryEvaluatePointForTargetedGather(val3, out candTier, out candOffset, out candSlot, out candIsWall, out candIsSeed, out candThing)
						: TryEvaluatePointForDefaultGather(val3, inDungeon, out candTier, out candOffset, out candSlot, out candIsWall, out candIsSeed, out candThing));

					if (!matched || (candSlot < 0 && candSlot != SlotHandOrCurrent))
					{
						continue;
					}

					// 根据采集顺序模式计算生效层级：
					// - NearestFirst（就近采集，默认）：所有普通地面采集物（1=种子、2=采集、3=矿石）归为同一层级 1 按真实寻路路程就近采集；地牢敲普通墙/临时挖墙（4）排在地面采集物之后（层级 4）
					// - CategoryOrder（按顺序：种子→采集→挖矿）：严格按 1=种子 < 2=采集 < 3=挖矿 < 4=普通敲墙/临时挖墙 顺序执行
					int effectiveTier = ((gatherOrderMode == GatherOrderMode.NearestFirst && candTier <= 3) ? 1 : candTier);
					if (effectiveTier > bestTier)
					{
						continue;
					}

					// 对已锁定的当前采集点赋予 -2.5f 粘滞优先权，防止被同伴换位顶退 1 格后在左右两个等距目标之间来回摇摆
					float stickinessBonus = ((_lastGatherTarget != null && _lastGatherTarget.x == i && _lastGatherTarget.z == j) ? (-2.5f) : 0f);
					// 快速下界粗筛：真实行走路程必定 >= max(0, 直线距离 - 1.5f)
					float minPossibleDist = Mathf.Max(0f, GetDistance(pcX, pcZ, i, j) - 1.5f);
					if (effectiveTier == bestTier && minPossibleDist + candOffset + stickinessBonus >= bestScore)
					{
						continue;
					}

					// 查询真实绕墙行走路程（O(1) 查距离场表），彻底告别挑中一墙之隔却要绕半个地牢的采集物
					if (!TryGetTargetPathDistance(pc, val3, 1, combat: false, allowPassThroughHostile: false, out var pathDistance))
					{
						_unreachableGatherTargets[item] = now + 8f;
						continue;
					}

					float candScore = pathDistance + candOffset + stickinessBonus;
					if (effectiveTier == bestTier && candScore >= bestScore)
					{
						continue;
					}

					bestTier = effectiveTier;
					bestScore = candScore;
					bestPoint = val3;
					bestSlot = candSlot;
					bestIsWall = candIsWall;
					bestIsSeed = candIsSeed;
					bestSpecificThing = candThing;
				}
			}
		}
		finally
		{
			GrowSystem.cell = prevGrowCell;
		}

		if (_unreachableGatherTargets.Count > 1000)
		{
			_unreachableGatherTargets.Clear();
		}

		if (bestPoint != null)
		{
			neededSlot = bestSlot;
			isWallTarget = bestIsWall;
			isSeedTarget = bestIsSeed;
			specificThing = bestSpecificThing;
			return bestPoint;
		}
		return null;
	}

	private void EnsureAppropriateToolEquippedForHarvest(Chara pc, int slot, bool isSeedTarget)
	{
		if (slot >= 0)
		{
			ToolSwitcher.SwitchSlot(slot);
			_lastEquippedSlot = slot;
		}
		else if (slot == SlotHandOrCurrent)
		{
			Thing curTool = ((Card)pc).Tool;
			bool blockedByTool = curTool != null && (curTool.trait is TraitToolBrush || curTool.trait is TraitToolShears || curTool.trait is TraitToolWaterCan || curTool.trait is TraitToolMusic || curTool.trait is TraitAbility || !curTool.trait.CanHarvest);
			if (blockedByTool || EClass.player?.currentHotItem is HotItemAct)
			{
				ToolSwitcher.ClearCurrentToolSelection();
				_lastEquippedSlot = -1;
			}
		}
		// 若当前并非收割种子（例如勾选的是"🕸️ 采集: 蘑菇"），必须确保手上拿的不是镰刀，防止 TaskHarvest.IsReapSeed 把蘑菇变成蘑菇孢子种子
		if (!isSeedTarget && ((Card)pc).Tool != null && (((Card)pc).Tool.trait is TraitToolSickle || ((Card)pc).Tool.category.IsChildOf("scythe")))
		{
			ToolSwitcher.ClearCurrentToolSelection();
			_lastEquippedSlot = -1;
		}
	}

	private TaskHarvest TryBuildAndValidateHarvestAct(Chara pc, Point target, int slot, bool isSeedTarget = false)
	{
		EnsureAppropriateToolEquippedForHarvest(pc, slot, isSeedTarget);
		TaskHarvest act = TaskHarvest.TryGetAct(pc, target);
		if (act != null)
		{
			((AIAct)act).owner = pc;
			((BaseTaskHarvest)act).SetTarget(pc, ((Card)pc).Tool);
			int reqEle = ((BaseTaskHarvest)act).idEle;
			if (DoesToolMatchHarvestRequirement(((Card)pc).Tool, reqEle, isSeedTarget) && !((BaseTaskHarvest)act).IsTooHard)
			{
				return act;
			}
		}
		return null;
	}

	private void RecordGatherTargetFailure(string targetKey)
	{
		_gatherFailCounts.TryGetValue(targetKey, out var fails);
		fails++;
		_gatherFailCounts[targetKey] = fails;
		if (fails >= MaxGatherRetry)
		{
			_failedTargets.Add(targetKey);
			TrimFailedTargets();
			_unreachableGatherTargets.Remove(targetKey);
		}
		else
		{
			_unreachableGatherTargets[targetKey] = Time.time + 15f;
		}
	}

	private bool DoGather(Chara pc, Point target, int neededSlot, bool isWallTarget, bool isSeedTarget = false, Thing specificHarvestThing = null)
	{
		string text = target.x + "," + target.z;
		// 若正在执行 TaskHarvest / TaskMine，或正在走向当前同一采集点的落脚格且尚未到达相邻可交互位置，保持当前动作
		if (IsBusyGathering(pc))
		{
			if (pc.ai is TaskHarvest || pc.ai is TaskMine)
			{
				return true;
			}
			if (pc.ai is AI_Goto && _lastGatherTarget != null && _lastGatherTarget.Equals(target)
				&& (((Card)pc).pos.Distance(target) > 1 || !CanInteractBetweenPoints(((Card)pc).pos, target, allowBlockedTarget: target.IsBlocked)))
			{
				return true;
			}
		}

		if (_lastGatherTarget != null && _lastGatherTarget.x == target.x && _lastGatherTarget.z == target.z)
		{
			if (!IsAIRunning(pc))
			{
				_gatherRetryCount++;
				_gatherFailCounts.TryGetValue(text, out var prevFails);
				if (_gatherRetryCount >= MaxGatherRetry || prevFails + 1 >= MaxGatherRetry)
				{
					_failedTargets.Add(text);
					TrimFailedTargets();
					ClearGatherProgressTracking();
					return false;
				}
			}
		}
		else
		{
			// 如果上一个采集点已被成功采空，或者刚完成收割种子（HasObj 仍在但 CanReapSeed 已变为 false），重置位移锚点并清理其失败计数
			if (_lastGatherTarget != null)
			{
				bool prevCleared = (!_lastGatherTarget.HasObj && !_lastGatherTarget.HasBlock)
					|| (_lastGatherTarget.cell != null && !_lastGatherTarget.cell.CanReapSeed());
				if (prevCleared)
				{
					_gatherFailCounts.Remove(_lastGatherTarget.x + "," + _lastGatherTarget.z);
					ResetProgressAnchor(pc);
				}
			}
			_gatherRetryCount = 0;
		}

		if (neededSlot >= 0 && (_lastEquippedSlot != neededSlot || !ToolSwitcher.IsSlotActuallyEquipped(neededSlot)))
		{
			ToolSwitcher.SwitchSlot(neededSlot);
			_lastEquippedSlot = neededSlot;
		}
		else if (neededSlot == SlotHandOrCurrent)
		{
			EnsureAppropriateToolEquippedForHarvest(pc, neededSlot, isSeedTarget);
		}

		// 非敲墙目标：执行 TaskHarvest（收割种子、采集地面/墙面附着 Obj、蘑菇、花草、骸骨残骸、矿石、以及格内 Thing 如蜘蛛网）
		if (!isWallTarget)
		{
			TaskHarvest val2 = null;

			// 如果明确指定了格内可采集 Thing（如专项收集中的蜘蛛网，或同格既有 Obj 又有 Thing），优先针对该 Thing 构建 TaskHarvest
			// 严格依据 TaskHarvest.OnCreateProgress (TaskHarvest.cs:292)：onProgressBegin 会校验 TaskHarvest.TryGetAct(owner, pos) != null，
			// 因此必须同时满足 TaskHarvest.TryGetAct(pc, target) != null，否则进入进度条瞬间会被原版 p.Cancel() 打断引发重试死循环！
			if (specificHarvestThing != null && !((Card)specificHarvestThing).isDestroyed && ((Card)specificHarvestThing).ExistsOnMap)
			{
				EnsureAppropriateToolEquippedForHarvest(pc, neededSlot, isSeedTarget: false);
				if (TaskHarvest.TryGetAct(pc, target) != null)
				{
					TaskHarvest thingAct = new TaskHarvest
					{
						pos = target.Copy(),
						mode = BaseTaskHarvest.HarvestType.Thing,
						target = specificHarvestThing
					};
					((AIAct)thingAct).owner = pc;
					((BaseTaskHarvest)thingAct).SetTarget(pc, ((Card)pc).Tool);
					if (DoesToolMatchHarvestRequirement(((Card)pc).Tool, ((BaseTaskHarvest)thingAct).idEle, isSeedTarget: false) && !((BaseTaskHarvest)thingAct).IsTooHard)
					{
						val2 = thingAct;
					}
				}
			}

			if (val2 == null)
			{
				bool hasObjHarvest = TryGetObjHarvestInfo(target, out var objReqEle);
				bool hasThingHarvest = TryGetThingHarvestInfo(target, out var thingReqEle, out var targetHarvestThing, onlyTargeted: TargetedGatherEnabled);
				bool hasSeedHarvest = isSeedTarget && SickleSlot >= 0 && target.cell != null && target.cell.CanReapSeed();

				if (hasObjHarvest || hasThingHarvest || hasSeedHarvest)
				{
					val2 = TryBuildAndValidateHarvestAct(pc, target, neededSlot, isSeedTarget);

					// 仅当备选槽位确实持有匹配目标所需技能元素的工具时才允许切换，彻底禁止挖铜矿失败时切到斧头！
					if (val2 == null && !isSeedTarget)
					{
						int reqEleForAlt = (hasObjHarvest ? objReqEle : thingReqEle);
						int altSlot1 = ((neededSlot == MiningSlot) ? ChoppingSlot : MiningSlot);
						if (altSlot1 >= 0 && altSlot1 != neededSlot && DoesSlotHoldMatchingTool(altSlot1, reqEleForAlt, isSeedTarget: false))
						{
							val2 = TryBuildAndValidateHarvestAct(pc, target, altSlot1, isSeedTarget: false);
						}
					}

					if (val2 == null && !isSeedTarget && hasThingHarvest && targetHarvestThing != null)
					{
						int thingSlot = GetGatherSlotForMaterialAndElement(targetHarvestThing.material, thingReqEle > 0 ? thingReqEle : 250);
						if (thingSlot >= 0 || thingSlot == SlotHandOrCurrent)
						{
							EnsureAppropriateToolEquippedForHarvest(pc, thingSlot, isSeedTarget: false);
							if (TaskHarvest.TryGetAct(pc, target) != null)
							{
								TaskHarvest thingAct2 = new TaskHarvest
								{
									pos = target.Copy(),
									mode = BaseTaskHarvest.HarvestType.Thing,
									target = targetHarvestThing
								};
								((AIAct)thingAct2).owner = pc;
								((BaseTaskHarvest)thingAct2).SetTarget(pc, ((Card)pc).Tool);
								if (DoesToolMatchHarvestRequirement(((Card)pc).Tool, ((BaseTaskHarvest)thingAct2).idEle, isSeedTarget: false) && !((BaseTaskHarvest)thingAct2).IsTooHard)
								{
									val2 = thingAct2;
								}
							}
						}
					}
				}
			}

			if (val2 != null)
			{
				_lastGatherTarget = target;
				_lastMineWallTarget = null;
				// 始终先用 SafeAI_Goto(gatherStandPt, 0) 精确走到无陷阱、无隔墙的安全相邻落脚格，绝不直接调用原版 TaskHarvest 内置的 DoGoto(destDist=1) 踩上陷阱！
				bool needWalkToGatherStand = ((Card)pc).pos.Distance(target) > 1
					|| !CanInteractBetweenPoints(((Card)pc).pos, target, allowBlockedTarget: target.IsBlocked)
					|| (EffectiveAvoidTraps && HasTrapOnCell(((Card)pc).pos.cell, pc));
				if (needWalkToGatherStand)
				{
					if (!TryGetReachableInteractStandPoint(pc, target, allowStandOnTarget: !target.IsBlocked, combat: false, out var gatherStandPt) || gatherStandPt == null || !StartSafeGoto(pc, gatherStandPt, 0, false))
					{
						RecordGatherTargetFailure(text);
						ClearGatherProgressTracking();
						return false;
					}
					return true;
				}
				if (!StartPCActImmediate(pc, (AIAct)(object)val2))
				{
					RecordGatherTargetFailure(text);
					ClearGatherProgressTracking();
					return false;
				}
				ResetProgressAnchor(pc);
				return true;
			}

			_failedTargets.Add(text);
			TrimFailedTargets();
			ClearGatherProgressTracking();
			return false;
		}

		// 敲墙目标（临时挖墙模块 / 地牢自动敲墙 / 野外特殊矿块 / 专项墙体）：执行 TaskMine
		if (target.HasBlock && neededSlot >= 0)
		{
			if (_lastEquippedSlot != neededSlot || !ToolSwitcher.IsSlotActuallyEquipped(neededSlot))
			{
				ToolSwitcher.SwitchSlot(neededSlot);
				_lastEquippedSlot = neededSlot;
			}
			bool flag = CanCurrentToolMineTarget(pc, target);
			if (!flag)
			{
				ToolSwitcher.SwitchSlot(neededSlot);
				_lastEquippedSlot = neededSlot;
				flag = CanCurrentToolMineTarget(pc, target);
			}
			if (!flag)
			{
				int altMineSlot = ((neededSlot == MiningSlot) ? ChoppingSlot : MiningSlot);
				if (altMineSlot >= 0 && altMineSlot != neededSlot)
				{
					Thing altThing = ToolSwitcher.GetSlotThing(altMineSlot);
					bool altCanMine = altThing != null && (((Card)altThing).HasElement(220, false) || ((Card)altThing).HasElement(230, false) || ((target.matBlock?.category == "wood" || target.matBlock?.category == "grass") && ((Card)altThing).HasElement(225, false)));
					if (altCanMine)
					{
						ToolSwitcher.SwitchSlot(altMineSlot);
						_lastEquippedSlot = altMineSlot;
						flag = CanCurrentToolMineTarget(pc, target);
					}
				}
			}
			if (!flag)
			{
				Log("Custom CanMine failed at " + text + " Tool=" + ((((Card)pc).Tool != null) ? ((Card)((Card)pc).Tool).Name : "null"));
				_failedTargets.Add(text);
				TrimFailedTargets();
				ClearGatherProgressTracking();
				return false;
			}
			// 先通过 SafeAI_Goto 走到墙体相邻的无陷阱、无隔墙安全落脚格，再启动挖掘动作
			bool needWalkToWallStand = ((Card)pc).pos.Distance(target) > 1
				|| !CanInteractBetweenPoints(((Card)pc).pos, target, allowBlockedTarget: true)
				|| (EffectiveAvoidTraps && HasTrapOnCell(((Card)pc).pos.cell, pc));
			if (needWalkToWallStand)
			{
				_lastGatherTarget = target;
				if (!TryGetReachableInteractStandPoint(pc, target, allowStandOnTarget: false, combat: false, out var wallStandPt) || wallStandPt == null || !StartSafeGoto(pc, wallStandPt, 0, false))
				{
					RecordGatherTargetFailure(text);
					ClearGatherProgressTracking();
					return false;
				}
				return true;
			}
			// 若墙体表面挂有壁挂附着物 (IsBlockMount)，先剥离附着物或直接挖掘
			if (target.HasObj && target.sourceObj != null && target.sourceObj.tileType != null && target.sourceObj.tileType.IsBlockMount)
			{
				TaskHarvest mountAct = TaskHarvest.TryGetAct(pc, target);
				if (mountAct != null)
				{
					((AIAct)mountAct).owner = pc;
					((BaseTaskHarvest)mountAct).SetTarget(pc, ((Card)pc).Tool);
					if (!((BaseTaskHarvest)mountAct).IsTooHard)
					{
						_lastGatherTarget = target;
						if (StartPCActImmediate(pc, (AIAct)(object)mountAct))
						{
							return true;
						}
					}
				}
				EClass._map.MineBlock(target, false, pc, true);
				_gatherFailCounts.Remove(text);
				ResetProgressAnchor(pc);
				ClearGatherProgressTracking();
				return true;
			}
			TaskMine val = new TaskMine
			{
				pos = target.Copy()
			};
			((AIAct)val).owner = pc;
			((BaseTaskHarvest)val).SetTarget(pc, (Thing)null);
			if (((BaseTaskHarvest)val).IsTooHard)
			{
				Log("TaskMine too hard at " + text);
				_failedTargets.Add(text);
				TrimFailedTargets();
				ClearGatherProgressTracking();
				return false;
			}
			_lastGatherTarget = target;
			_lastMineWallTarget = target.Copy();
			if (!StartPCActImmediate(pc, (AIAct)(object)val))
			{
				RecordGatherTargetFailure(text);
				ClearGatherProgressTracking();
				return false;
			}
			return true;
		}

		_failedTargets.Add(text);
		TrimFailedTargets();
		ClearGatherProgressTracking();
		return false;
	}

	private bool CanCurrentToolMineTarget(Chara pc, Point target)
	{
		if (((Card)pc).Tool == null || target.matBlock == null)
		{
			return false;
		}
		if (((Card)((Card)pc).Tool).HasElement(220, false) || ((Card)((Card)pc).Tool).HasElement(230, false))
		{
			return true;
		}
		if ((target.matBlock.category == "wood" || target.matBlock.category == "grass") && ((Card)((Card)pc).Tool).HasElement(225, false))
		{
			return true;
		}
		return false;
	}

	private void TrimFailedTargets()
	{
		if (_failedTargets.Count > MaxFailedTargets)
		{
			_failedTargets.Clear();
		}
	}

	private string SerializeTargetedGatherItems()
	{
		if (TargetedGatherKeys.Count == 0)
		{
			return "";
		}
		StringBuilder sb = new StringBuilder();
		foreach (string key in TargetedGatherKeys)
		{
			if (string.IsNullOrEmpty(key))
			{
				continue;
			}
			if (sb.Length > 0)
			{
				sb.Append(';');
			}
			string label = "";
			if (TargetedGatherLabels.TryGetValue(key, out var l) && !string.IsNullOrEmpty(l))
			{
				label = l.Replace(";", " ").Replace("|", " ").Replace("\n", " ").Replace("\r", "");
			}
			sb.Append(key).Append('|').Append(label);
		}
		return sb.ToString();
	}

	private void DeserializeTargetedGatherItems(string raw)
	{
		_legacyTargetKeysUpgraded = false;
		TargetedGatherKeys.Clear();
		TargetedGatherLabels.Clear();
		if (string.IsNullOrWhiteSpace(raw))
		{
			return;
		}
		string[] entries = raw.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < entries.Length; i++)
		{
			string entry = entries[i].Trim();
			if (string.IsNullOrEmpty(entry))
			{
				continue;
			}
			int bar = entry.IndexOf('|');
			if (bar > 0)
			{
				string k = entry.Substring(0, bar).Trim();
				string v = ((bar < entry.Length - 1) ? entry.Substring(bar + 1).Trim() : "");
				if (!string.IsNullOrEmpty(k))
				{
					TargetedGatherKeys.Add(k);
					if (!string.IsNullOrEmpty(v))
					{
						TargetedGatherLabels[k] = v;
					}
				}
			}
			else
			{
				TargetedGatherKeys.Add(entry);
			}
		}
	}

	/// <summary>序列化基础设置预设的全部参数（包含快捷栏、阈值、打怪与拾取优先级、更多设置等全部基础设置项）。</summary>
	public string SerializeBasicPresetContent()
	{
		StringBuilder sb = new StringBuilder();
		sb.Append("CombatSlot=").Append(CombatSlot).Append('\n');
		sb.Append("MiningSlot=").Append(MiningSlot).Append('\n');
		sb.Append("ChoppingSlot=").Append(ChoppingSlot).Append('\n');
		sb.Append("DisassembleSlot=").Append(DisassembleSlot).Append('\n');
		sb.Append("SickleSlot=").Append(SickleSlot).Append('\n');
		sb.Append("KiteHpPercent=").Append(KiteHpPercent.ToString(CultureInfo.InvariantCulture)).Append('\n');
		sb.Append("RetreatHpPercent=").Append(RetreatHpPercent.ToString(CultureInfo.InvariantCulture)).Append('\n');
		sb.Append("MaxStamina=").Append(MaxStamina).Append('\n');
		sb.Append("BurdenAutoStopEnabled=").Append(StopOnBurdenEnabled).Append('\n');
		sb.Append("MaxBurdenPercent=").Append(MaxBurdenPercent).Append('\n');
		sb.Append("CombatScanMode=").Append(combatScanMode).Append('\n');
		sb.Append("CombatScanRadius=").Append(CombatScanRadius).Append('\n');
		sb.Append("CombatTargetPriority=").Append(combatTargetPriority).Append('\n');
		sb.Append("ExplorePriority=").Append(explorePriority).Append('\n');
		sb.Append("AttackChicken=").Append(AttackChicken).Append('\n');
		sb.Append("FishingProtectionEnabled=").Append(FishingProtectionEnabled).Append('\n');
		sb.Append("IdleAutoStopEnabled=").Append(IdleAutoStopEnabled).Append('\n');
		sb.Append("IdleAutoStopSeconds=").Append(IdleAutoStopSeconds).Append('\n');
		sb.Append("DebugMode=").Append(DebugMode).Append('\n');
		sb.Append("UseCustomSearchBounds=").Append(UseCustomSearchBounds).Append('\n');
		sb.Append("SearchMinX=").Append(SearchMinX).Append('\n');
		sb.Append("SearchMaxX=").Append(SearchMaxX).Append('\n');
		sb.Append("SearchMinZ=").Append(SearchMinZ).Append('\n');
		sb.Append("SearchMaxZ=").Append(SearchMaxZ).Append('\n');
		return sb.ToString();
	}

	/// <summary>应用基础设置键值对行。</summary>
	private void ApplyBasicPresetLines(string[] lines)
	{
		if (lines == null)
		{
			return;
		}
		foreach (string line in lines)
		{
			if (string.IsNullOrEmpty(line))
			{
				continue;
			}
			int eqIdx = line.IndexOf('=');
			if (eqIdx <= 0)
			{
				continue;
			}
			string k = line.Substring(0, eqIdx).Trim();
			string v = ((eqIdx < line.Length - 1) ? line.Substring(eqIdx + 1).Trim() : "");
			try
			{
				switch (k)
				{
				case "CombatSlot":
					CombatSlot = int.Parse(v);
					break;
				case "MiningSlot":
					MiningSlot = int.Parse(v);
					break;
				case "ChoppingSlot":
					ChoppingSlot = int.Parse(v);
					break;
				case "DisassembleSlot":
					DisassembleSlot = int.Parse(v);
					break;
				case "SickleSlot":
					SickleSlot = int.Parse(v);
					break;
				case "KiteHpPercent":
					KiteHpPercent = float.Parse(v, CultureInfo.InvariantCulture);
					break;
				case "RetreatHpPercent":
					RetreatHpPercent = float.Parse(v, CultureInfo.InvariantCulture);
					break;
				case "MaxStamina":
					MaxStamina = int.Parse(v);
					break;
				case "BurdenAutoStopEnabled":
				case "StopOnBurdenEnabled":
					StopOnBurdenEnabled = bool.Parse(v);
					break;
				case "MaxBurdenPercent":
					MaxBurdenPercent = Mathf.Clamp(int.Parse(v), 50, 500);
					break;
				case "CombatScanMode":
					combatScanMode = (CombatScanMode)Enum.Parse(typeof(CombatScanMode), v);
					break;
				case "CombatScanRadius":
					CombatScanRadius = int.Parse(v);
					break;
				case "CombatTargetPriority":
					combatTargetPriority = (CombatTargetPriority)Enum.Parse(typeof(CombatTargetPriority), v);
					break;
				case "ExplorePriority":
					explorePriority = (ExploreActionPriority)Enum.Parse(typeof(ExploreActionPriority), v);
					break;
				case "AttackChicken":
					AttackChicken = bool.Parse(v);
					break;
				case "FishingProtectionEnabled":
					FishingProtectionEnabled = bool.Parse(v);
					break;
				case "IdleAutoStopEnabled":
					IdleAutoStopEnabled = bool.Parse(v);
					break;
				case "IdleAutoStopSeconds":
					IdleAutoStopSeconds = Mathf.Clamp(int.Parse(v), 3, 300);
					break;
				case "DebugMode":
					DebugMode = bool.Parse(v);
					_debugEnabled = DebugMode;
					break;
				case "UseCustomSearchBounds":
					UseCustomSearchBounds = bool.Parse(v);
					break;
				case "SearchMinX":
					SearchMinX = int.Parse(v);
					break;
				case "SearchMaxX":
					SearchMaxX = int.Parse(v);
					break;
				case "SearchMinZ":
					SearchMinZ = int.Parse(v);
					break;
				case "SearchMaxZ":
					SearchMaxZ = int.Parse(v);
					break;
				}
			}
			catch (Exception ex)
			{
				Log("ApplyBasicPresetLines skipped (" + k + "=" + v + "): " + ex.Message);
			}
		}
	}

	/// <summary>返回当前所有已存在的基础设置预设名（"默认"排第一，其余按自然顺序排列）。</summary>
	public List<string> GetBasicPresetNames()
	{
		List<string> list = new List<string>();
		try
		{
			Directory.CreateDirectory(_basicPresetsDir);
			foreach (string path in Directory.GetFiles(_basicPresetsDir, "*.txt"))
			{
				string name = Path.GetFileNameWithoutExtension(path);
				if (!string.IsNullOrWhiteSpace(name))
				{
					list.Add(name);
				}
			}
		}
		catch (Exception ex)
		{
			Log("GetBasicPresetNames error: " + ex.Message);
		}
		if (list.Count == 0)
		{
			list.Add(DefaultPresetName);
		}
		list.Sort(ComparePresetNamesNatural);
		return list;
	}

	/// <summary>显式保存当前基础设置到当前生效的基础设置预设文件（点击基础预设的【保存】或【新增】时调用）。</summary>
	public void SaveCurrentBasicPreset()
	{
		try
		{
			Directory.CreateDirectory(_basicPresetsDir);
			string sanitized = SanitizePresetName(ActiveBasicPresetName);
			ActiveBasicPresetName = sanitized;
			string content = SerializeBasicPresetContent();
			File.WriteAllText(GetBasicPresetFilePath(ActiveBasicPresetName), content);
			_savedBasicPresetSnapshot = content;
			SaveActiveBasicPresetPointer();
			SaveConfig();
			Log("Saved basic preset: " + ActiveBasicPresetName);
		}
		catch (Exception ex)
		{
			Log("SaveCurrentBasicPreset error: " + ex.Message);
		}
	}

	/// <summary>切换到一个已存在的基础设置预设（不自动覆盖旧预设未点【保存】的临时改动，直接加载目标预设并同步 config.txt）。</summary>
	public bool SwitchToBasicPreset(string presetName)
	{
		if (string.IsNullOrWhiteSpace(presetName))
		{
			return false;
		}
		ActiveBasicPresetName = SanitizePresetName(presetName);
		LoadBasicPresetFromDisk(ActiveBasicPresetName);
		SaveActiveBasicPresetPointer();
		SaveConfig();
		Log("Switched to basic preset: " + ActiveBasicPresetName);
		return true;
	}

	/// <summary>将当前基础设置另存为新预设（或重置为默认值新预设），立即切换为当前生效基础预设并写入磁盘。</summary>
	public bool SaveAsNewBasicPreset(string presetName, bool resetToDefault = false)
	{
		if (string.IsNullOrWhiteSpace(presetName))
		{
			return false;
		}
		if (resetToDefault)
		{
			CombatSlot = -1;
			MiningSlot = -1;
			ChoppingSlot = -1;
			DisassembleSlot = -1;
			SickleSlot = -1;
			KiteHpPercent = 0.5f;
			RetreatHpPercent = 0.15f;
			MaxStamina = 5;
			StopOnBurdenEnabled = false;
			MaxBurdenPercent = 120;
			combatScanMode = CombatScanMode.FullMap;
			CombatScanRadius = 15;
			combatTargetPriority = CombatTargetPriority.Nearest;
			explorePriority = ExploreActionPriority.MonsterFirst;
			AttackChicken = false;
			FishingProtectionEnabled = true;
			IdleAutoStopEnabled = true;
			IdleAutoStopSeconds = 10;
			DebugMode = false;
			_debugEnabled = false;
			UseCustomSearchBounds = false;
			SearchMinX = 36;
			SearchMaxX = 64;
			SearchMinZ = 36;
			SearchMaxZ = 64;
		}
		ActiveBasicPresetName = SanitizePresetName(presetName);
		SaveCurrentBasicPreset();
		return true;
	}

	/// <summary>放弃当前基础预设中尚未点击【保存】的改动，回退到该预设上次保存的状态。</summary>
	public void RevertCurrentBasicPreset()
	{
		LoadBasicPresetFromDisk(ActiveBasicPresetName);
		SaveConfig();
	}

	/// <summary>删除一个基础设置预设文件。至少保留一个预设；若删除的是当前生效预设则自动切换到第一个可用预设。</summary>
	public bool DeleteBasicPreset(string presetName)
	{
		try
		{
			List<string> all = GetBasicPresetNames();
			if (all.Count <= 1)
			{
				return false;
			}
			string sanitized = SanitizePresetName(presetName);
			string path = GetBasicPresetFilePath(sanitized);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			if (string.Equals(ActiveBasicPresetName, sanitized, StringComparison.OrdinalIgnoreCase))
			{
				string fallback = GetBasicPresetNames().FirstOrDefault() ?? DefaultPresetName;
				SwitchToBasicPreset(fallback);
			}
			return true;
		}
		catch (Exception ex)
		{
			Log("DeleteBasicPreset error: " + ex.Message);
			return false;
		}
	}

	/// <summary>重命名一个基础设置预设。</summary>
	public bool RenameBasicPreset(string oldName, string newName)
	{
		if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
		{
			return false;
		}
		string sanitizedOld = SanitizePresetName(oldName);
		string sanitizedNew = SanitizePresetName(newName);
		if (string.Equals(sanitizedOld, sanitizedNew, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		try
		{
			string oldPath = GetBasicPresetFilePath(sanitizedOld);
			string newPath = GetBasicPresetFilePath(sanitizedNew);
			if (!File.Exists(oldPath) || File.Exists(newPath))
			{
				return false;
			}
			File.Move(oldPath, newPath);
			if (string.Equals(ActiveBasicPresetName, sanitizedOld, StringComparison.OrdinalIgnoreCase))
			{
				ActiveBasicPresetName = sanitizedNew;
				SaveActiveBasicPresetPointer();
			}
			return true;
		}
		catch (Exception ex)
		{
			Log("RenameBasicPreset error: " + ex.Message);
			return false;
		}
	}

	private void SaveActiveBasicPresetPointer()
	{
		try
		{
			Directory.CreateDirectory(_modRootPath);
			File.WriteAllText(_activeBasicPresetPointerPath, ActiveBasicPresetName);
		}
		catch (Exception ex)
		{
			Log("SaveActiveBasicPresetPointer error: " + ex.Message);
		}
	}

	private void LoadActiveBasicPresetPointer()
	{
		try
		{
			if (File.Exists(_activeBasicPresetPointerPath))
			{
				string text = File.ReadAllText(_activeBasicPresetPointerPath).Trim();
				if (!string.IsNullOrEmpty(text))
				{
					ActiveBasicPresetName = SanitizePresetName(text);
					return;
				}
			}
		}
		catch (Exception ex)
		{
			Log("LoadActiveBasicPresetPointer error: " + ex.Message);
		}
		ActiveBasicPresetName = DefaultPresetName;
	}

	private void LoadBasicPresetFromDisk(string presetName)
	{
		try
		{
			string path = GetBasicPresetFilePath(presetName);
			if (File.Exists(path))
			{
				string[] lines = File.ReadAllLines(path);
				ApplyBasicPresetLines(lines);
				_savedBasicPresetSnapshot = SerializeBasicPresetContent();
			}
			else
			{
				Directory.CreateDirectory(_basicPresetsDir);
				string content = SerializeBasicPresetContent();
				File.WriteAllText(path, content);
				_savedBasicPresetSnapshot = content;
			}
		}
		catch (Exception ex)
		{
			Log("LoadBasicPresetFromDisk error: " + ex.Message);
		}
	}

	/// <summary>获取指定基础设置预设的简短摘要（供下拉菜单显示）。</summary>
	public string GetBasicPresetSummary(string presetName)
	{
		try
		{
			string path = GetBasicPresetFilePath(presetName);
			if (!File.Exists(path))
			{
				return "";
			}
			int configuredSlots = 0;
			int maxStam = 5;
			foreach (string line in File.ReadAllLines(path))
			{
				if (string.IsNullOrEmpty(line)) continue;
				int eq = line.IndexOf('=');
				if (eq <= 0) continue;
				string k = line.Substring(0, eq).Trim();
				string v = ((eq < line.Length - 1) ? line.Substring(eq + 1).Trim() : "");
				if ((k == "CombatSlot" || k == "MiningSlot" || k == "ChoppingSlot" || k == "DisassembleSlot" || k == "SickleSlot") && int.TryParse(v, out var sv) && sv >= 0)
				{
					configuredSlots++;
				}
				else if (k == "MaxStamina" && int.TryParse(v, out var ms))
				{
					maxStam = ms;
				}
			}
			return $"槽位:{configuredSlots}/5 · 体力:{maxStam}";
		}
		catch
		{
			return "";
		}
	}

	/// <summary>保存全局通用设置（快捷栏、阈值、采集开关、专项总开关等）到 config.txt。
	/// 注意：专项勾选材料列表（TargetedGatherKeys）由独立的专项预设文件管理，基础设置预设（BasicPresets）也需点击【保存】才更新预设快照。</summary>
	public void SaveConfig()
	{
		try
		{
			Directory.CreateDirectory(_modRootPath);
			GatherCategory persistedCategory = ((gatherCategory == GatherCategory.Dungeon) ? GatherCategory.Dungeon : ((gatherCategory == GatherCategory.None) ? GatherCategory.None : GatherCategory.Field));
			bool persistedDungeonMode = persistedCategory == GatherCategory.Dungeon;
			string contents = $"CombatSlot={CombatSlot}\nMiningSlot={MiningSlot}\nChoppingSlot={ChoppingSlot}\nDisassembleSlot={DisassembleSlot}\nSickleSlot={SickleSlot}\nKiteHpPercent={KiteHpPercent.ToString(CultureInfo.InvariantCulture)}\nRetreatHpPercent={RetreatHpPercent.ToString(CultureInfo.InvariantCulture)}\nMaxStamina={MaxStamina}\nAttackChicken={AttackChicken}\nUseCustomSearchBounds={UseCustomSearchBounds}\nSearchMinX={SearchMinX}\nSearchMaxX={SearchMaxX}\nSearchMinZ={SearchMinZ}\nSearchMaxZ={SearchMaxZ}\nGatherCategory={persistedCategory}\nFieldChop={FieldChop}\nFieldMine={FieldMine}\nFieldCrops={FieldCrops}\nIgnoreDeepWater={IgnoreDeepWater}\nSwimModeEnabled={SwimModeEnabled}\nSwimRetreatOxygenPercent={SwimRetreatOxygenPercent}\nDungeonMode={persistedDungeonMode}\nDungeonChop={DungeonChop}\nDungeonMineObjs={DungeonMineObjs}\nDungeonCrops={DungeonCrops}\nDungeonMineWalls={DungeonMineWalls}\nDungeonMineWallLimit={DungeonMineWallLimit}\nBurdenAutoStopEnabled={StopOnBurdenEnabled}\nMaxBurdenPercent={MaxBurdenPercent}\nAvoidTraps={AvoidTraps}\nDisarmTraps={DisarmTraps}\nFishingProtectionEnabled={FishingProtectionEnabled}\nIdleAutoStopEnabled={IdleAutoStopEnabled}\nIdleAutoStopSeconds={IdleAutoStopSeconds}\nTargetedGatherEnabled={TargetedGatherEnabled}\nDebugMode={DebugMode}\nAutoPickEnabled={autoPickEnabled}\nGatherOrderMode={gatherOrderMode}\nCombatTargetPriority={combatTargetPriority}\nExplorePriority={explorePriority}\nCombatScanMode={combatScanMode}\nCombatScanRadius={CombatScanRadius}\n";
			File.WriteAllText(_generalConfigPath, contents);
		}
		catch (Exception ex)
		{
			Log("SaveConfig error: " + ex.Message);
		}
	}

	/// <summary>显式保存当前专项收集勾选列表到当前选中的专项预设文件（仅在用户点击【保存】或【新增】按钮时调用）。</summary>
	public void SaveCurrentTargetedPreset()
	{
		try
		{
			Directory.CreateDirectory(_presetsDir);
			string sanitized = SanitizePresetName(ActivePresetName);
			ActivePresetName = sanitized;
			string serializedTargets = SerializeTargetedGatherItems();
			string contents = $"TargetedGatherItems={serializedTargets}\n";
			File.WriteAllText(GetPresetFilePath(ActivePresetName), contents);
			SyncSavedTargetedSnapshotFromCurrent();
			_presetItemCountCache[ActivePresetName] = TargetedGatherKeys.Count;
			SaveActivePresetPointer();
			Log("Saved targeted preset: " + ActivePresetName + $" ({TargetedGatherKeys.Count} items)");
		}
		catch (Exception ex)
		{
			Log("SaveCurrentTargetedPreset error: " + ex.Message);
		}
	}

	private void SyncSavedTargetedSnapshotFromCurrent()
	{
		_savedTargetedGatherKeys.Clear();
		foreach (string k in TargetedGatherKeys)
		{
			_savedTargetedGatherKeys.Add(k);
		}
		_savedTargetedGatherLabels.Clear();
		foreach (KeyValuePair<string, string> kvp in TargetedGatherLabels)
		{
			_savedTargetedGatherLabels[kvp.Key] = kvp.Value;
		}
	}

	/// <summary>放弃当前专项预设中尚未点击【保存】的改动，回退到该预设上次保存的状态。</summary>
	public void RevertCurrentTargetedPreset()
	{
		LoadTargetedPresetFromDisk(ActivePresetName);
		_failedTargets.Clear();
		_unreachableGatherTargets.Clear();
		_gatherFailCounts.Clear();
	}

	/// <summary>获取指定专项预设已保存在磁盘中的条目数（供下拉菜单展示）。</summary>
	public int GetPresetSavedItemCount(string presetName)
	{
		string sanitized = SanitizePresetName(presetName);
		if (_presetItemCountCache.TryGetValue(sanitized, out var count))
		{
			return count;
		}
		int loadedCount = ReadPresetItemCountFromDisk(sanitized);
		_presetItemCountCache[sanitized] = loadedCount;
		return loadedCount;
	}

	private int ReadPresetItemCountFromDisk(string sanitizedPresetName)
	{
		try
		{
			string path = GetPresetFilePath(sanitizedPresetName);
			if (!File.Exists(path))
			{
				return 0;
			}
			string[] lines = File.ReadAllLines(path);
			foreach (string line in lines)
			{
				if (string.IsNullOrEmpty(line))
				{
					continue;
				}
				int eq = line.IndexOf('=');
				if (eq > 0)
				{
					string k = line.Substring(0, eq).Trim();
					if (string.Equals(k, "TargetedGatherItems", StringComparison.OrdinalIgnoreCase))
					{
						string raw = ((eq < line.Length - 1) ? line.Substring(eq + 1).Trim() : "");
						if (string.IsNullOrWhiteSpace(raw))
						{
							return 0;
						}
						string[] entries = raw.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
						int valid = 0;
						for (int i = 0; i < entries.Length; i++)
						{
							if (!string.IsNullOrWhiteSpace(entries[i]))
							{
								valid++;
							}
						}
						return valid;
					}
				}
			}
		}
		catch
		{
		}
		return 0;
	}

	/// <summary>自然排序比较器：保证"默认"排第一，其余按数字序号自然排序（例如 预设1, 预设2, ..., 预设10）。</summary>
	private static int ComparePresetNamesNatural(string a, string b)
	{
		if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}
		if (string.Equals(a, DefaultPresetName, StringComparison.OrdinalIgnoreCase))
		{
			return -1;
		}
		if (string.Equals(b, DefaultPresetName, StringComparison.OrdinalIgnoreCase))
		{
			return 1;
		}
		int ia = 0;
		int ib = 0;
		while (ia < a.Length && ib < b.Length)
		{
			if (char.IsDigit(a[ia]) && char.IsDigit(b[ib]))
			{
				long va = 0L;
				while (ia < a.Length && char.IsDigit(a[ia]))
				{
					va = va * 10 + (a[ia] - '0');
					ia++;
				}
				long vb = 0L;
				while (ib < b.Length && char.IsDigit(b[ib]))
				{
					vb = vb * 10 + (b[ib] - '0');
					ib++;
				}
				if (va != vb)
				{
					return va.CompareTo(vb);
				}
			}
			else
			{
				char ca = char.ToUpperInvariant(a[ia]);
				char cb = char.ToUpperInvariant(b[ib]);
				if (ca != cb)
				{
					return ca.CompareTo(cb);
				}
				ia++;
				ib++;
			}
		}
		return a.Length.CompareTo(b.Length);
	}

	/// <summary>返回当前所有已存在的专项预设名（"默认"排第一，其余按自然顺序排列）。</summary>
	public List<string> GetPresetNames()
	{
		List<string> list = new List<string>();
		try
		{
			Directory.CreateDirectory(_presetsDir);
			foreach (string path in Directory.GetFiles(_presetsDir, "*.txt"))
			{
				string name = Path.GetFileNameWithoutExtension(path);
				if (!string.IsNullOrWhiteSpace(name))
				{
					list.Add(name);
				}
			}
		}
		catch (Exception ex)
		{
			Log("GetPresetNames error: " + ex.Message);
		}
		if (list.Count == 0)
		{
			list.Add(DefaultPresetName);
		}
		list.Sort(ComparePresetNamesNatural);
		return list;
	}

	/// <summary>切换到一个已存在的专项预设：不保存旧预设中未点【保存】的临时改动（方便选错时直接切换回退），直接读取目标预设的专项材料列表。</summary>
	public bool SwitchToPreset(string presetName)
	{
		if (string.IsNullOrWhiteSpace(presetName))
		{
			return false;
		}
		ActivePresetName = SanitizePresetName(presetName);
		LoadTargetedPresetFromDisk(ActivePresetName);
		_failedTargets.Clear();
		_unreachableGatherTargets.Clear();
		_gatherFailCounts.Clear();
		SaveActivePresetPointer();
		Log("Switched to targeted preset (without auto-saving old): " + ActivePresetName);
		return true;
	}

	/// <summary>把当前内存里的专项勾选另存为一个新预设（或创建空白新预设），立即切换为生效预设并写入该新预设文件（原预设文件保持不变）。</summary>
	public bool SaveAsNewPreset(string presetName, bool startEmpty = false)
	{
		if (string.IsNullOrWhiteSpace(presetName))
		{
			return false;
		}
		if (startEmpty)
		{
			TargetedGatherKeys.Clear();
			TargetedGatherLabels.Clear();
		}
		ActivePresetName = SanitizePresetName(presetName);
		SaveCurrentTargetedPreset();
		_failedTargets.Clear();
		_unreachableGatherTargets.Clear();
		_gatherFailCounts.Clear();
		Log("Saved as new targeted preset: " + ActivePresetName);
		return true;
	}

	/// <summary>删除一个专项预设文件。至少保留一个预设，不允许删空；删除的是当前生效预设时会自动切换到另一个。</summary>
	public bool DeletePreset(string presetName)
	{
		try
		{
			List<string> all = GetPresetNames();
			if (all.Count <= 1)
			{
				Log("DeletePreset skipped: only one preset left");
				return false;
			}
			string sanitized = SanitizePresetName(presetName);
			string path = GetPresetFilePath(sanitized);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			_presetItemCountCache.Remove(sanitized);
			if (string.Equals(ActivePresetName, sanitized, StringComparison.OrdinalIgnoreCase))
			{
				string fallback = GetPresetNames().FirstOrDefault() ?? DefaultPresetName;
				SwitchToPreset(fallback);
			}
			return true;
		}
		catch (Exception ex)
		{
			Log("DeletePreset error: " + ex.Message);
			return false;
		}
	}

	/// <summary>重命名一个专项预设。如果重命名的正好是当前生效预设，会同步更新 ActivePresetName 和指针文件。</summary>
	public bool RenamePreset(string oldName, string newName)
	{
		if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
		{
			return false;
		}
		string sanitizedOld = SanitizePresetName(oldName);
		string sanitizedNew = SanitizePresetName(newName);
		if (string.Equals(sanitizedOld, sanitizedNew, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		try
		{
			string oldPath = GetPresetFilePath(sanitizedOld);
			string newPath = GetPresetFilePath(sanitizedNew);
			if (!File.Exists(oldPath))
			{
				Log("RenamePreset error: source not found " + oldPath);
				return false;
			}
			if (File.Exists(newPath))
			{
				Log("RenamePreset error: target already exists " + newPath);
				return false;
			}
			File.Move(oldPath, newPath);
			if (_presetItemCountCache.TryGetValue(sanitizedOld, out var cnt))
			{
				_presetItemCountCache.Remove(sanitizedOld);
				_presetItemCountCache[sanitizedNew] = cnt;
			}
			if (string.Equals(ActivePresetName, sanitizedOld, StringComparison.OrdinalIgnoreCase))
			{
				ActivePresetName = sanitizedNew;
				SaveActivePresetPointer();
			}
			Log("Renamed targeted preset " + sanitizedOld + " -> " + sanitizedNew);
			return true;
		}
		catch (Exception ex)
		{
			Log("RenamePreset error: " + ex.Message);
			return false;
		}
	}

	private void SaveActivePresetPointer()
	{
		try
		{
			Directory.CreateDirectory(_modRootPath);
			File.WriteAllText(_activePresetPointerPath, ActivePresetName);
		}
		catch (Exception ex)
		{
			Log("SaveActivePresetPointer error: " + ex.Message);
		}
	}

	private void LoadActivePresetPointer()
	{
		try
		{
			if (File.Exists(_activePresetPointerPath))
			{
				string text = File.ReadAllText(_activePresetPointerPath).Trim();
				if (!string.IsNullOrEmpty(text))
				{
					ActivePresetName = SanitizePresetName(text);
					return;
				}
			}
		}
		catch (Exception ex)
		{
			Log("LoadActivePresetPointer error: " + ex.Message);
		}
		ActivePresetName = DefaultPresetName;
	}

	/// <summary>一次性平滑迁移：
	/// 1. 若全局 config.txt 尚不存在，从旧版 Presets 目录（或旧写死路径）迁移通用设置；
	/// 2. 若专项预设目录 TargetedPresets 尚不存在，从旧版 Presets 目录提取各预设的 TargetedGatherItems 迁移为独立专项预设。</summary>
	private void MigrateLegacyConfigIfNeeded()
	{
		try
		{
			Directory.CreateDirectory(_modRootPath);

			// 1) 迁移全局通用配置 config.txt
			if (!File.Exists(_generalConfigPath))
			{
				string sourceGeneralFile = null;
				if (File.Exists(_activePresetPointerPath))
				{
					string ptr = File.ReadAllText(_activePresetPointerPath).Trim();
					if (!string.IsNullOrEmpty(ptr))
					{
						string candidate = Path.Combine(_legacyPresetsDir, SanitizePresetName(ptr) + ".txt");
						if (File.Exists(candidate))
						{
							sourceGeneralFile = candidate;
						}
					}
				}
				if (sourceGeneralFile == null && Directory.Exists(_legacyPresetsDir))
				{
					string defaultCandidate = Path.Combine(_legacyPresetsDir, DefaultPresetName + ".txt");
					if (File.Exists(defaultCandidate))
					{
						sourceGeneralFile = defaultCandidate;
					}
					else
					{
						string[] legacyFiles = Directory.GetFiles(_legacyPresetsDir, "*.txt");
						if (legacyFiles.Length > 0)
						{
							sourceGeneralFile = legacyFiles[0];
						}
					}
				}
				if (sourceGeneralFile == null && File.Exists(LegacyConfigPath))
				{
					sourceGeneralFile = LegacyConfigPath;
				}
				if (sourceGeneralFile != null)
				{
					File.Copy(sourceGeneralFile, _generalConfigPath, overwrite: true);
					Log("Migrated general config from " + sourceGeneralFile);
				}
			}

			// 2) 迁移专项预设目录 TargetedPresets
			if (!Directory.Exists(_presetsDir) || Directory.GetFiles(_presetsDir, "*.txt").Length == 0)
			{
				Directory.CreateDirectory(_presetsDir);
				bool migratedAny = false;
				if (Directory.Exists(_legacyPresetsDir))
				{
					foreach (string legacyFile in Directory.GetFiles(_legacyPresetsDir, "*.txt"))
					{
						string pName = Path.GetFileNameWithoutExtension(legacyFile);
						if (string.IsNullOrWhiteSpace(pName))
						{
							continue;
						}
						string targetedLine = "TargetedGatherItems=";
						foreach (string line in File.ReadAllLines(legacyFile))
						{
							if (line != null && line.TrimStart().StartsWith("TargetedGatherItems=", StringComparison.OrdinalIgnoreCase))
							{
								targetedLine = line.Trim();
								break;
							}
						}
						File.WriteAllText(GetPresetFilePath(pName), targetedLine + "\n");
						migratedAny = true;
					}
				}
				if (!migratedAny)
				{
					string targetedLine = "TargetedGatherItems=";
					if (File.Exists(_generalConfigPath))
					{
						foreach (string line in File.ReadAllLines(_generalConfigPath))
						{
							if (line != null && line.TrimStart().StartsWith("TargetedGatherItems=", StringComparison.OrdinalIgnoreCase))
							{
								targetedLine = line.Trim();
								break;
							}
						}
					}
					File.WriteAllText(GetPresetFilePath(DefaultPresetName), targetedLine + "\n");
				}
			}
		}
		catch (Exception ex)
		{
			Log("MigrateLegacyConfig error: " + ex.Message);
		}
	}

	public void LoadConfig()
	{
		MigrateLegacyConfigIfNeeded();
		LoadActivePresetPointer();
		LoadActiveBasicPresetPointer();
		LoadGeneralConfig();
		List<string> allBasicPresets = GetBasicPresetNames();
		if (!allBasicPresets.Any(p => string.Equals(p, ActiveBasicPresetName, StringComparison.OrdinalIgnoreCase)))
		{
			ActiveBasicPresetName = allBasicPresets.FirstOrDefault() ?? DefaultPresetName;
			SaveActiveBasicPresetPointer();
		}
		LoadBasicPresetFromDisk(ActiveBasicPresetName);
		List<string> allPresets = GetPresetNames();
		if (!allPresets.Any(p => string.Equals(p, ActivePresetName, StringComparison.OrdinalIgnoreCase)))
		{
			ActivePresetName = allPresets.FirstOrDefault() ?? DefaultPresetName;
			SaveActivePresetPointer();
		}
		LoadTargetedPresetFromDisk(ActivePresetName);
		_presetItemCountCache.Clear();
		foreach (string p in allPresets)
		{
			_presetItemCountCache[p] = ReadPresetItemCountFromDisk(p);
		}
		ResetTempWallOptions();
	}

	private void LoadTargetedPresetFromDisk(string presetName)
	{
		_legacyTargetKeysUpgraded = false;
		TargetedGatherKeys.Clear();
		TargetedGatherLabels.Clear();
		try
		{
			string path = GetPresetFilePath(presetName);
			if (File.Exists(path))
			{
				string[] lines = File.ReadAllLines(path);
				foreach (string line in lines)
				{
					if (string.IsNullOrEmpty(line))
					{
						continue;
					}
					int eqIdx = line.IndexOf('=');
					if (eqIdx > 0)
					{
						string k = line.Substring(0, eqIdx).Trim();
						string v = ((eqIdx < line.Length - 1) ? line.Substring(eqIdx + 1).Trim() : "");
						if (string.Equals(k, "TargetedGatherItems", StringComparison.OrdinalIgnoreCase))
						{
							DeserializeTargetedGatherItems(v);
						}
					}
				}
			}
			else
			{
				Directory.CreateDirectory(_presetsDir);
				File.WriteAllText(path, "TargetedGatherItems=\n");
			}
		}
		catch (Exception ex)
		{
			Log("LoadTargetedPresetFromDisk error: " + ex.Message);
		}
		SyncSavedTargetedSnapshotFromCurrent();
		UpgradeLegacyTargetedGatherKeysIfReady();
		_presetItemCountCache[SanitizePresetName(presetName)] = TargetedGatherKeys.Count;
	}

	private void LoadGeneralConfig()
	{
		ResetTempWallOptions();
		TargetedGatherEnabled = false;
		try
		{
			if (!File.Exists(_generalConfigPath))
			{
				return;
			}
			string[] array = File.ReadAllLines(_generalConfigPath);
			bool hasExplicitGatherCategory = false;
			bool hasExplicitFieldCrops = false;
			foreach (string text in array)
			{
				if (string.IsNullOrEmpty(text))
				{
					continue;
				}
				int eqIdx = text.IndexOf('=');
				if (eqIdx > 0)
				{
					string text2 = text.Substring(0, eqIdx).Trim();
					string text3 = ((eqIdx < text.Length - 1) ? text.Substring(eqIdx + 1).Trim() : "");
					try
					{
						switch (text2)
						{
						case "CombatSlot":
							CombatSlot = int.Parse(text3);
							break;
						case "MiningSlot":
							MiningSlot = int.Parse(text3);
							break;
						case "ChoppingSlot":
							ChoppingSlot = int.Parse(text3);
							break;
						case "DisassembleSlot":
							DisassembleSlot = int.Parse(text3);
							break;
						case "SickleSlot":
							SickleSlot = int.Parse(text3);
							break;
						case "KiteHpPercent":
							KiteHpPercent = float.Parse(text3, CultureInfo.InvariantCulture);
							break;
						case "RetreatHpPercent":
							RetreatHpPercent = float.Parse(text3, CultureInfo.InvariantCulture);
							break;
						case "MaxStamina":
							MaxStamina = int.Parse(text3);
							break;
						case "AttackChicken":
							AttackChicken = bool.Parse(text3);
							break;
						case "UseCustomSearchBounds":
							UseCustomSearchBounds = bool.Parse(text3);
							break;
						case "SearchMinX":
							SearchMinX = int.Parse(text3);
							break;
						case "SearchMaxX":
							SearchMaxX = int.Parse(text3);
							break;
						case "SearchMinZ":
							SearchMinZ = int.Parse(text3);
							break;
						case "SearchMaxZ":
							SearchMaxZ = int.Parse(text3);
							break;
						case "GatherCategory":
						{
							GatherCategory parsedCat = (GatherCategory)Enum.Parse(typeof(GatherCategory), text3);
							gatherCategory = ((parsedCat == GatherCategory.Dungeon) ? GatherCategory.Dungeon : ((parsedCat == GatherCategory.None) ? GatherCategory.None : GatherCategory.Field));
							hasExplicitGatherCategory = true;
							break;
						}
						case "FieldChop":
							FieldChop = bool.Parse(text3);
							break;
						case "FieldMine":
							FieldMine = bool.Parse(text3);
							break;
						case "FieldCrops":
							FieldCrops = bool.Parse(text3);
							hasExplicitFieldCrops = true;
							break;
						case "IgnoreDeepWater":
							IgnoreDeepWater = bool.Parse(text3);
							break;
						case "SwimModeEnabled":
							SwimModeEnabled = bool.Parse(text3);
							break;
						case "SwimRetreatOxygenPercent":
							SwimRetreatOxygenPercent = Mathf.Clamp(int.Parse(text3), 15, 90);
							break;
						case "CollectCrops":
							if (!hasExplicitFieldCrops)
							{
								FieldCrops = bool.Parse(text3);
							}
							break;
						case "DungeonMode":
							if (!hasExplicitGatherCategory)
							{
								gatherCategory = (bool.Parse(text3) ? GatherCategory.Dungeon : GatherCategory.Field);
							}
							break;
						case "DungeonChop":
							DungeonChop = bool.Parse(text3);
							break;
						case "DungeonMineObjs":
							DungeonMineObjs = bool.Parse(text3);
							break;
						case "DungeonCrops":
							DungeonCrops = bool.Parse(text3);
							break;
						case "DungeonSubType":
						case "DungeonAutoDetect":
							break;
						case "DungeonMineWalls":
							DungeonMineWalls = bool.Parse(text3);
							break;
						case "DungeonMineWallLimit":
							DungeonMineWallLimit = Mathf.Clamp(int.Parse(text3), 0, 9999);
							break;
						case "BurdenAutoStopEnabled":
							StopOnBurdenEnabled = bool.Parse(text3);
							break;
						case "MaxBurdenPercent":
							MaxBurdenPercent = Mathf.Clamp(int.Parse(text3), 50, 500);
							break;
						case "AvoidTraps":
							AvoidTraps = bool.Parse(text3);
							break;
						case "DisarmTraps":
							DisarmTraps = bool.Parse(text3);
							break;
						case "FishingProtectionEnabled":
							FishingProtectionEnabled = bool.Parse(text3);
							break;
						case "IdleAutoStopEnabled":
							IdleAutoStopEnabled = bool.Parse(text3);
							break;
						case "IdleAutoStopSeconds":
							IdleAutoStopSeconds = Mathf.Clamp(int.Parse(text3), 3, 300);
							break;
						case "TargetedGatherEnabled":
							TargetedGatherEnabled = bool.Parse(text3);
							break;
						case "TargetedGatherItems":
						case "StopOnBurdenEnabled":
						case "MineWalls":
						case "MineWallsWithAxe":
						case "PickupMode":
							break;
						case "DebugMode":
							DebugMode = bool.Parse(text3);
							_debugEnabled = DebugMode;
							break;
						case "AutoPickEnabled":
							autoPickEnabled = bool.Parse(text3);
							break;
						case "GatherOrderMode":
							gatherOrderMode = (GatherOrderMode)Enum.Parse(typeof(GatherOrderMode), text3);
							break;
						case "CombatTargetPriority":
							combatTargetPriority = (CombatTargetPriority)Enum.Parse(typeof(CombatTargetPriority), text3);
							break;
						case "ExplorePriority":
							explorePriority = (ExploreActionPriority)Enum.Parse(typeof(ExploreActionPriority), text3);
							break;
						case "CombatScanMode":
							combatScanMode = (CombatScanMode)Enum.Parse(typeof(CombatScanMode), text3);
							break;
						case "CombatScanRadius":
							CombatScanRadius = int.Parse(text3);
							break;
						}
					}
					catch (Exception ex2)
					{
						Log("LoadGeneralConfig line skipped (\"" + text2 + "=" + text3 + "\"): " + ex2.Message);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log("LoadGeneralConfig error: " + ex.Message);
		}
		ResetTempWallOptions();
	}

	private float GetDistance(Chara a, Chara b)
	{
		return GetDistance(((Card)a).pos.x, ((Card)a).pos.z, ((Card)b).pos.x, ((Card)b).pos.z);
	}

	private float GetDistance(int x1, int z1, int x2, int z2)
	{
		int num = x1 - x2;
		int num2 = z1 - z2;
		return Mathf.Sqrt((float)(num * num + num2 * num2));
	}

	public void ClearPickupState()
	{
		targetPickup = null;
		failedPickups.Clear();
		_pickupFailCounts.Clear();
		nextPickScanTime = 0f;
		_lastPickTargetUid = -1;
		_pickRetryCount = 0;
		_lastPickDist = float.MaxValue;
	}

	/// <summary>对齐原版 Chara.Pick 的背包可放入判定（支持子背包/容器及可堆叠物品，避免顶层背包格满时误判完全不能捡东西）。</summary>
	private bool CanPickThingToInventory(Chara pc, Thing thing)
	{
		if (pc == null || thing == null || ((Card)thing).isDestroyed)
		{
			return false;
		}
		try
		{
			return ((Card)pc).things.GetDest(thing, true).IsValid;
		}
		catch
		{
			return !((Card)pc).things.IsFull(thing);
		}
	}

	private Thing ScanForPickup(float maxPathDistance = -1f)
	{
		Chara pc = EClass.pc;
		if (pc == null || EClass._map == null)
		{
			return null;
		}
		GetGatherScanBounds(out var minX, out var maxX, out var minZ, out var maxZ);
		// 先预热单源寻路距离场，使所有战利品的真实绕墙行走距离查询为 O(1)
		EnsureSafeDistanceField(pc);
		int pcX = ((Card)pc).pos.x;
		int pcZ = ((Card)pc).pos.z;
		Thing result = null;
		float num6 = float.MaxValue;
		float currentTime = Time.time;
		foreach (Thing thing in EClass._map.things)
		{
			if (thing == null || ((Card)thing).isDestroyed || !((Card)thing).ExistsOnMap || (int)((Card)thing).placeState != 0 || ((Card)thing).ignoreAutoPick || ((Card)thing).isMasked || ((Card)thing).isHidden)
			{
				continue;
			}
			// 地面上的可采集物（如蜘蛛网 TraitCobWeb 等）交给采集逻辑处理，不走普通拾取
			if (thing.trait != null && !string.IsNullOrEmpty(thing.trait.ReqHarvest))
			{
				continue;
			}
			Point pos = ((Card)thing).pos;
			if (pos == null || !pos.IsValid || !pos.IsInBounds || pos.x < minX || pos.x > maxX || pos.z < minZ || pos.z > maxZ)
			{
				continue;
			}
			Cell itemCell = pos.cell;
			if (itemCell == null || itemCell.outOfBounds)
			{
				continue;
			}
			if (EffectiveAvoidTraps && HasVisibleTrapOnCell(itemCell, pc))
			{
				continue;
			}
			if (IsForbiddenDeepWater(itemCell, pc) && !HasWalkableNeighbor(pos, pc))
			{
				continue;
			}
			int uid = ((Card)thing).uid;
			if (failedPickups.TryGetValue(uid, out var failTime))
			{
				if (currentTime < failTime || currentTime - failTime < 3f)
				{
					continue;
				}
				failedPickups.Remove(uid);
			}
			if (!CanPickThingToInventory(pc, thing))
			{
				failedPickups[uid] = currentTime + 5f;
				continue;
			}
			float euclidDist = GetDistance(pcX, pcZ, pos.x, pos.z);
			if (maxPathDistance > 0f && euclidDist > maxPathDistance + 1.5f)
			{
				continue;
			}
			float stickyBonus = ((targetPickup == thing) ? (-1.5f) : 0f);
			float minPossibleDist = Mathf.Max(0f, euclidDist - 1.5f) + stickyBonus;
			if (minPossibleDist >= num6)
			{
				continue;
			}
			// 按真实绕墙行走路程评估拾取物距离，彻底避免挑中一墙之隔却要绕一大圈的掉落物
			if (!TryGetTargetPathDistance(pc, pos, 1, combat: false, allowPassThroughHostile: true, out var pathDistance))
			{
				failedPickups[uid] = currentTime + 6f;
				continue;
			}
			if (maxPathDistance > 0f && pathDistance > maxPathDistance)
			{
				continue;
			}
			float score = pathDistance + stickyBonus;
			if (score < num6)
			{
				num6 = score;
				result = thing;
			}
		}
		if (failedPickups.Count > 200)
		{
			foreach (int item in (from kvp in failedPickups
				where currentTime > kvp.Value && currentTime - kvp.Value > 6f
				select kvp.Key).ToList())
			{
				failedPickups.Remove(item);
			}
		}
		return result;
	}

	private bool TryPickupItem(Thing thing)
	{
		if (thing == null || ((Card)thing).isDestroyed || EClass.pc == null)
		{
			return false;
		}
		if (!((Card)thing).ExistsOnMap || (int)((Card)thing).placeState != 0)
		{
			return false;
		}
		// 注意：不调用 EClass.pc.CanPick(thing)，因为原版 Chara.CanPick 内部调用 things.IsFull(thing) 仅检查顶层背包格，
		// 当顶层背包满而子背包/容器仍有空位时，ScanForPickup(GetDest) 判定可捡但 CanPick 会返回 false，引发反复跑向掉落物却不捡的死循环！
		Card rootCard = ((Card)thing).GetRootCard();
		if (rootCard == null || rootCard.isDestroyed || (rootCard.ExistsOnMap && rootCard.pos.Distance(((Card)EClass.pc).pos) > 1))
		{
			return false;
		}
		if (!CanPickThingToInventory(EClass.pc, thing))
		{
			return false;
		}
		try
		{
			EClass.pc.Pick(thing, true, true);
			return ((Card)thing).isDestroyed || !((Card)thing).ExistsOnMap || (int)((Card)thing).placeState != 0;
		}
		catch
		{
			return false;
		}
	}

	public float GetNextPickScanTime()
	{
		return nextPickScanTime;
	}
}