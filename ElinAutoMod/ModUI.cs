using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ElinAutoMod;

[DefaultExecutionOrder(-100)]
public class ModUI : MonoBehaviour
{
	private bool _showWindow;
	private bool _minimized;
	// 默认主窗口拓宽加长（450×760），以左上角为锚点展开/收起
	private Rect _windowRect = new Rect(20f, 20f, 450f, 760f);
	private bool _initialScreenClampDone;

	// 专项收集独立子窗口状态与矩形
	private bool _showTargetedSubWindow;
	private Rect _targetedWindowRect = new Rect(480f, 20f, 430f, 680f);
	private bool _targetedSubWindowPosInitialized;
	private Vector2 _targetedWindowScroll = Vector2.zero;

	private AutoController _ctrl;

	// 篮球/拉杆机功能：独立组件，不属于 AutoController 的自动脚本体系
	private BasketballController _bball;

	private float _opacity = 0.92f;

	private Vector2 _scrollPosition = Vector2.zero;

	// 主导航栏（按高频到低频重组，零重复项）：采集 | 专项（内嵌为主，可按需弹出独立子窗） | 战斗 | 配置 | 金玲
	private enum SettingsTab { Gather, Targeted, Combat, Config, Basketball }
	private SettingsTab _activeTab = SettingsTab.Gather;

	// 基础设置内部子视图（全部统一受基础设置预设控制）
	private enum BasicSubSection { All, Slots, Threshold, More }
	private BasicSubSection _basicSubSection = BasicSubSection.All;

	// ---------- 专项收集子窗口内部状态 ----------
	private enum TargetedSubTab { CurrentMap, Seed, Tree, Gather, Mine }
	private TargetedSubTab _targetedSubTab = TargetedSubTab.CurrentMap;
	private string _targetedSearchText = "";
	private Vector2 _targetedSelectedScroll = Vector2.zero;
	private Vector2 _targetedCatalogScroll = Vector2.zero;
	private List<AutoController.CatalogEntry> _cachedCatalog = new List<AutoController.CatalogEntry>();
	private TargetedSubTab _cachedCatalogTab = (TargetedSubTab)(-1);
	private Map _cachedCatalogMap;
	private int _cachedCatalogDbCount = -1;
	private float _lastCatalogScanTime = -999f;

	// ---------- 专项预设管理 UI 状态 ----------
	private float _saveFeedbackUntil = -1f;
	private bool _targetedPresetDropdownOpen;
	private string _editingPresetOldName;
	private bool _creatingPreset;
	private bool _pendingFocusPresetInput;
	private string _presetInlineBuffer = "";
	private Vector2 _presetScrollPosition = Vector2.zero;

	// ---------- 基础设置预设管理 UI 状态 ----------
	private float _basicSaveFeedbackUntil = -1f;
	private bool _basicPresetDropdownOpen;
	private string _editingBasicPresetOldName;
	private bool _creatingBasicPreset;
	private bool _pendingFocusBasicPresetInput;
	private string _basicPresetInlineBuffer = "";
	private Vector2 _basicPresetScrollPosition = Vector2.zero;

	// ---------- 状态栏（主窗口顶部状态条 & 缩小迷你状态框）快捷切换预设下拉状态 ----------
	private bool _statusBasicDropdownOpen;
	private Vector2 _statusBasicDropdownScroll = Vector2.zero;
	private bool _statusTargetedDropdownOpen;
	private Vector2 _statusTargetedDropdownScroll = Vector2.zero;

	private enum MiniPopupMode { None, BasicPreset, TargetedPreset }
	private MiniPopupMode _miniPopupMode = MiniPopupMode.None;
	private Vector2 _miniPopupScroll = Vector2.zero;

	private static readonly string[] _textFieldControlNames = new string[]
	{
		"presetInlineEdit",
		"basicPresetInlineEdit",
		"targetedSearchField",
		"burdenPctField",
		"wallLimitField",
		"minXField",
		"maxXField",
		"minZField",
		"maxZField",
		"lever1Field",
		"lever2Field",
		"lever3Field"
	};

	// 最小化完整状态框的位置与拖拽状态（展开箭头在最左侧，打开/关闭严格以左上角为锚点）
	private float _miniPosX = -1f;
	private float _miniPosY = -1f;
	private const float MiniExpandWidth = 34f;
	private const float MiniStatusWidth = 158f;
	private const float MiniBasicPresetWidth = 86f;
	private const float MiniTargetToggleWidth = 70f;
	private const float MiniTargetPresetWidth = 86f;
	private const float MiniDungeonNavWidth = 108f;
	private const float MiniBballWidth = 58f;
	private const float MiniTotalWidth = MiniExpandWidth + MiniStatusWidth + MiniBasicPresetWidth + MiniTargetToggleWidth + MiniTargetPresetWidth + MiniDungeonNavWidth + MiniBballWidth;
	private const float MiniBoxHeight = 42f;
	private bool _miniPointerDown;
	private bool _miniIsDragging;
	private Vector2 _miniMouseDownPos = Vector2.zero;
	private Vector2 _miniDragStartPos = Vector2.zero;

	// 主窗口右下角拖拽缩放状态
	private bool _isResizingCorner;
	private Vector2 _resizeGrabOffset = Vector2.zero;
	private const float MinWidth = 390f;
	private const float MinHeight = 420f;
	private const float MaxWidth = 860f;
	private const float MaxHeight = 1050f;
	private const float ResizeGripWidth = 46f;
	private const float ResizeGripHeight = 32f;
	private const float ResizeOuterMargin = 14f;
	private bool _resizeHover;

	// 专项子窗口右下角拖拽缩放状态
	private bool _isResizingTargetedCorner;
	private Vector2 _targetedResizeGrabOffset = Vector2.zero;
	private bool _targetedResizeHover;

	// 挂载在 EClass.ui.canvas 下的透明 uGUI 射线拦截层（layer = 5），
	// 使游戏原生 InputModuleEX 与 UI.RefreshActiveState() 自动识别鼠标正处于 UI 上方（isPointerOverUI = true），
	// 从而让 AM_Adv.OnUpdateCursor() 自动保持默认正上方 UI 指针（不再变成八方向移动箭头），并阻断地面行走/滚轮切工具。
	private GameObject _uiRaycastShieldObj;
	private RectTransform _uiRaycastShieldRect;
	private GameObject _uiTargetedRaycastShieldObj;
	private RectTransform _uiTargetedRaycastShieldRect;

	// 与 AutoController.State 枚举（Idle..Picking 共7个值）一一对应
	private string[] _stateNames = new string[7] { "空闲", "采集中", "战斗中", "风筝中", "撤退中", "收获中", "扫描拾取中" };

	// ---------- 配色方案A：奶油白粉奶咖（高可读、柔和耐看） ----------
	private Color _cream          = new Color(0.992f, 0.976f, 0.965f, 1f); // #FDF9F6 奶油白主底
	private Color _clay           = new Color(0.949f, 0.902f, 0.871f, 1f); // #F2E6DE 奶咖顶栏/悬浮条底
	private Color _rose           = new Color(0.973f, 0.882f, 0.898f, 1f); // #F8E1E5 浅樱粉悬浮态
	private Color _ink            = new Color(0.290f, 0.216f, 0.188f, 1f); // #4A3730 可可深棕正文
	private Color _inkSoft        = new Color(0.518f, 0.416f, 0.380f, 1f); // #846A61 柔奶咖次级字
	private Color _moss           = new Color(0.435f, 0.612f, 0.475f, 1f); // #6F9C79 抹茶暖绿运行态
	private Color _line           = new Color(0.886f, 0.812f, 0.765f, 1f); // #E2CFC3 浅奶咖分割线
	private Color _sectionBg      = new Color(0.969f, 0.937f, 0.914f, 1f); // #F7EFE9 柔奶白卡片底
	private Color _selectedOrange = new Color(0.710f, 0.357f, 0.427f, 1f); // #B55B6D 深雾玫瑰强调字
	private Color _selectedBg     = new Color(0.831f, 0.486f, 0.553f, 1f); // #D47C8D 雾玫瑰粉选中按钮底
	private Color _selectedText   = new Color(1.000f, 0.992f, 0.992f, 1f); // #FFFDFD 玫瑰粉底上的纯白字

	// 保留原字段名做兼容
	private Color _bgColor      => _cream;
	private Color _headerColor  => _clay;
	private Color _accentColor  => _rose;
	private Color _btnColor     => new Color(0.949f, 0.902f, 0.871f, 1f); // #F2E6DE 奶咖常规按钮底
	private Color _textColor    => _ink;
	private Color _resizeHandleColor = new Color(0.663f, 0.573f, 0.537f, 1f);
	private Color _resizeHoverColor  = new Color(0.831f, 0.486f, 0.553f, 1f);

	private GUIStyle _boxStyle;
	private GUIStyle _headerStyle;
	private GUIStyle _labelStyle;
	private GUIStyle _labelSubStyle;
	private GUIStyle _fieldLabelStyle;
	private GUIStyle _btnStyle;
	private GUIStyle _miniStyle;
	private GUIStyle _sectionStyle;
	private GUIStyle _toggleStyle;
	private GUIStyle _slotCellStyle;
	private GUIStyle _tabStyle;
	private GUIStyle _chipStyle;

	private Texture2D _bgTex;
	private Texture2D _headerTex;
	private Texture2D _btnTex;
	private Texture2D _btnHoverTex;
	private Texture2D _sectionTex;
	private Texture2D _miniTex;
	private Texture2D _runningBtnTex;
	private Texture2D _resizeHandleTex;
	private Texture2D _resizeHoverTex;
	private Texture2D _selectedBtnTex;
	private Texture2D _lineTex;

	private GUIStyle _resizeLabelStyle;
	private GUIStyle _resizeLabelHoverStyle;

	private bool _stylesReady;

	private void Start()
	{
		_ctrl = ((Component)this).GetComponent<AutoController>();
		_bball = ((Component)this).GetComponent<BasketballController>();
	}

	private bool IsModTextFieldFocused()
	{
		if (!_showWindow || _minimized)
		{
			return false;
		}
		if (_creatingPreset || _editingPresetOldName != null || _creatingBasicPreset || _editingBasicPresetOldName != null)
		{
			return true;
		}
		if (GUIUtility.keyboardControl == 0)
		{
			return false;
		}
		string focused = GUI.GetNameOfFocusedControl();
		if (!string.IsNullOrEmpty(focused))
		{
			for (int i = 0; i < _textFieldControlNames.Length; i++)
			{
				if (string.Equals(focused, _textFieldControlNames[i], StringComparison.Ordinal))
				{
					return true;
				}
			}
		}
		return true;
	}

	/// <summary>
	/// 强制接管 Unity IME 输入法状态：
	/// 游戏本体 Core.OnApplicationFocus 会调用 EInput.DisableIME() 将 Input.imeCompositionMode 置为 Off，
	/// 导致 Unity IMGUI 的 GUILayout.TextField 彻底无法唤起中文输入法（只能打出英文字母）。
	/// 此处在窗口展开且输入框激活/聚焦时强制置为 IMECompositionMode.On，并拦截游戏快捷键防止打字时误触角色移动或技能。
	/// </summary>
	private void SyncIMEAndTextInputState()
	{
		try
		{
			if (!_showWindow || _minimized)
			{
				if (Input.imeCompositionMode == IMECompositionMode.On)
				{
					Input.imeCompositionMode = IMECompositionMode.Auto;
				}
				return;
			}
			bool textActive = IsModTextFieldFocused();
			Input.imeCompositionMode = textActive ? IMECompositionMode.On : IMECompositionMode.Auto;
			if (textActive)
			{
				EInput.Consume(consumeAxis: true);
			}
		}
		catch
		{
		}
	}

	private void Update()
	{
		if (Input.GetKeyDown((KeyCode)293))
		{
			_showWindow = !_showWindow;
			if (!_showWindow)
			{
				_isResizingCorner = false;
				_isResizingTargetedCorner = false;
				_miniPointerDown = false;
				_miniIsDragging = false;
				_resizeHover = false;
				_targetedResizeHover = false;
				_creatingPreset = false;
				_editingPresetOldName = null;
				_pendingFocusPresetInput = false;
				_creatingBasicPreset = false;
				_editingBasicPresetOldName = null;
				_pendingFocusBasicPresetInput = false;
				_miniPopupMode = MiniPopupMode.None;
				_statusBasicDropdownOpen = false;
				_statusTargetedDropdownOpen = false;
				GUIUtility.keyboardControl = 0;
			}
		}
		SyncIMEAndTextInputState();
		SyncGameUICursorAndShield();
	}

	private void LateUpdate()
	{
		SyncIMEAndTextInputState();
		SyncGameUICursorAndShield();
	}

	/// <summary>获取缩小状态栏及其弹出下拉列表占据的屏幕矩形（IMGUI 坐标系，左上角为原点）。</summary>
	private Rect GetMiniInteractiveScreenRect()
	{
		float totalWidth = MiniTotalWidth;
		float boxHeight = MiniBoxHeight;
		float px = (_miniPosX < 0f) ? Mathf.Clamp(_windowRect.x, 4f, Mathf.Max(4f, (float)Screen.width - totalWidth - 4f)) : _miniPosX;
		float py = (_miniPosY < 0f) ? Mathf.Clamp(_windowRect.y, 4f, Mathf.Max(4f, (float)Screen.height - boxHeight - 4f)) : _miniPosY;
		if (_miniPopupMode != MiniPopupMode.None)
		{
			Rect popRect = GetMiniPopupRect(px, py, totalWidth, boxHeight);
			float minX = Mathf.Min(px, popRect.x);
			float minY = Mathf.Min(py, popRect.y);
			float maxX = Mathf.Max(px + totalWidth, popRect.xMax);
			float maxY = Mathf.Max(py + boxHeight, popRect.yMax);
			return new Rect(minX, minY, maxX - minX, maxY - minY);
		}
		return new Rect(px, py, totalWidth, boxHeight);
	}

	private Rect GetMiniPopupRect(float miniX, float miniY, float totalWidth, float boxHeight)
	{
		List<string> names = (_miniPopupMode == MiniPopupMode.BasicPreset)
			? ((_ctrl != null) ? _ctrl.GetBasicPresetNames() : new List<string>())
			: ((_ctrl != null) ? _ctrl.GetPresetNames() : new List<string>());
		float rowH = 26f;
		float popW = 240f;
		float popH = Mathf.Clamp((float)Mathf.Max(1, names.Count) * (rowH + 2f) + 34f, 64f, 220f);
		float anchorX = (_miniPopupMode == MiniPopupMode.BasicPreset)
			? (miniX + MiniExpandWidth + MiniStatusWidth)
			: (miniX + MiniExpandWidth + MiniStatusWidth + MiniBasicPresetWidth);
		float px = Mathf.Clamp(anchorX, 4f, Mathf.Max(4f, (float)Screen.width - popW - 4f));
		float py = miniY + boxHeight + 2f;
		if (py + popH > (float)Screen.height - 4f && miniY - popH - 2f >= 4f)
		{
			py = miniY - popH - 2f;
		}
		return new Rect(px, py, popW, popH);
	}

	/// <summary>获取当前主窗口（或最小化条）占据的屏幕矩形（IMGUI 坐标系，左上角为原点）。</summary>
	private Rect GetCurrentInteractiveScreenRect()
	{
		if (_minimized)
		{
			return GetMiniInteractiveScreenRect();
		}
		return new Rect(
			_windowRect.x,
			_windowRect.y,
			_windowRect.width + ResizeOuterMargin,
			_windowRect.height + ResizeOuterMargin);
	}

	/// <summary>获取专项收集独立子窗口占据的屏幕矩形。</summary>
	private Rect GetTargetedWindowInteractiveScreenRect()
	{
		return new Rect(
			_targetedWindowRect.x,
			_targetedWindowRect.y,
			_targetedWindowRect.width + ResizeOuterMargin,
			_targetedWindowRect.height + ResizeOuterMargin);
	}

	/// <summary>判断鼠标当前是否位于 ModUI 主窗口/专项子窗口/最小化条/缩放手柄热区内，或正在拖拽/缩放窗口。</summary>
	private bool IsPointerOverModUI()
	{
		if (!_showWindow)
		{
			return false;
		}
		if (_isResizingCorner || _isResizingTargetedCorner || _miniPointerDown)
		{
			return true;
		}
		Vector2 screenMouse = new Vector2(Input.mousePosition.x, (float)Screen.height - Input.mousePosition.y);
		if (_minimized)
		{
			return GetMiniInteractiveScreenRect().Contains(screenMouse);
		}
		if (_windowRect.Contains(screenMouse) || GetResizeHandleRect(_windowRect).Contains(screenMouse))
		{
			return true;
		}
		if (_showTargetedSubWindow && (_targetedWindowRect.Contains(screenMouse) || GetResizeHandleRect(_targetedWindowRect).Contains(screenMouse)))
		{
			return true;
		}
		return false;
	}

	private void EnsureShieldObject(ref GameObject obj, ref RectTransform rectTransform, string objName)
	{
		if ((UnityEngine.Object)(object)obj == (UnityEngine.Object)null)
		{
			obj = new GameObject(objName);
			obj.layer = 5; // Unity UI Layer (UI.RefreshActiveState 检测 item.layer == 5)
			rectTransform = obj.AddComponent<RectTransform>();
			Image shieldImg = obj.AddComponent<Image>();
			shieldImg.color = new Color(0f, 0f, 0f, 0f);
			shieldImg.raycastTarget = true;
			rectTransform.SetParent(((Component)EClass.ui.canvas).transform, false);
			rectTransform.anchorMin = Vector2.zero;
			rectTransform.anchorMax = Vector2.zero;
			rectTransform.pivot = new Vector2(0f, 1f);
			rectTransform.localScale = Vector3.one;
		}
		if (!obj.activeSelf)
		{
			obj.SetActive(true);
		}
		if (rectTransform.parent != ((Component)EClass.ui.canvas).transform)
		{
			rectTransform.SetParent(((Component)EClass.ui.canvas).transform, false);
		}
		rectTransform.SetAsLastSibling();
	}

	/// <summary>
	/// 同步挂载在 EClass.ui.canvas 下的透明 uGUI 拦截层（layer = 5），并在鼠标悬停于 ModUI 时：
	/// 1. 让 UI.RefreshActiveState() 原生判定 EClass.ui.isPointerOverUI = true；
	/// 2. 让 AM_Adv.OnUpdateCursor() 自动将光标设为默认正上方 UI 指针（null），不再变成八方向移动箭头或采集图标；
	/// 3. 拦截鼠标滚轮切快捷栏（EInput.wheel）与左右键地面移动（ActionMode.Adv.pressedAction）。
	/// </summary>
	private void SyncGameUICursorAndShield()
	{
		try
		{
			if (EClass.core == null || !EClass.core.initialized || (UnityEngine.Object)(object)EClass.ui == (UnityEngine.Object)null || (UnityEngine.Object)(object)EClass.ui.canvas == (UnityEngine.Object)null)
			{
				return;
			}

			if (!_showWindow)
			{
				if ((UnityEngine.Object)(object)_uiRaycastShieldObj != (UnityEngine.Object)null && _uiRaycastShieldObj.activeSelf)
				{
					_uiRaycastShieldObj.SetActive(false);
				}
				if ((UnityEngine.Object)(object)_uiTargetedRaycastShieldObj != (UnityEngine.Object)null && _uiTargetedRaycastShieldObj.activeSelf)
				{
					_uiTargetedRaycastShieldObj.SetActive(false);
				}
				return;
			}

			float scale = ((UnityEngine.Object)(object)EClass.ui.canvasScaler != (UnityEngine.Object)null && EClass.ui.canvasScaler.scaleFactor > 0.01f)
				? EClass.ui.canvasScaler.scaleFactor
				: ((EClass.ui.canvas.scaleFactor > 0.01f) ? EClass.ui.canvas.scaleFactor : 1f);

			EnsureShieldObject(ref _uiRaycastShieldObj, ref _uiRaycastShieldRect, "ElinAutoMod_UIRaycastShield");
			Rect activeRect = GetCurrentInteractiveScreenRect();
			_uiRaycastShieldRect.anchoredPosition = new Vector2(activeRect.x / scale, ((float)Screen.height - activeRect.y) / scale);
			_uiRaycastShieldRect.sizeDelta = new Vector2(activeRect.width / scale, activeRect.height / scale);

			if (!_minimized && _showTargetedSubWindow)
			{
				EnsureShieldObject(ref _uiTargetedRaycastShieldObj, ref _uiTargetedRaycastShieldRect, "ElinAutoMod_TargetedUIRaycastShield");
				Rect targetWinRect = GetTargetedWindowInteractiveScreenRect();
				_uiTargetedRaycastShieldRect.anchoredPosition = new Vector2(targetWinRect.x / scale, ((float)Screen.height - targetWinRect.y) / scale);
				_uiTargetedRaycastShieldRect.sizeDelta = new Vector2(targetWinRect.width / scale, targetWinRect.height / scale);
			}
			else if ((UnityEngine.Object)(object)_uiTargetedRaycastShieldObj != (UnityEngine.Object)null && _uiTargetedRaycastShieldObj.activeSelf)
			{
				_uiTargetedRaycastShieldObj.SetActive(false);
			}

			if (IsPointerOverModUI())
			{
				EClass.ui.isPointerOverUI = true;
				CursorSystem.leftIcon = null;
				if (CursorSystem.ignoreCount > 0)
				{
					CursorSystem.ignoreCount = 0;
				}
				CursorSystem.SetCursor(null);
				EInput.wheel = 0;

				if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || _isResizingCorner || _isResizingTargetedCorner || _miniPointerDown)
				{
					EInput.leftMouse.Consume();
					EInput.rightMouse.Consume();
					if (ActionMode.Adv != null && ActionMode.Adv.pressedAction != null && (ActionMode.Adv.pressedAction.button != null || ActionMode.Adv.pressedAction.action != null))
					{
						ActionMode.Adv.pressedAction.Init();
					}
				}
			}
		}
		catch
		{
		}
	}

	private void InitStyles()
	{
		if (_stylesReady) return;

		_bgTex          = MakeTex(2, 2, _cream);
		_headerTex      = MakeTex(2, 2, _clay);
		_btnTex         = MakeTex(2, 2, _btnColor);
		_btnHoverTex    = MakeTex(2, 2, _accentColor);
		_sectionTex     = MakeTex(2, 2, _sectionBg);
		_miniTex        = MakeTex(2, 2, _clay);
		_runningBtnTex  = MakeTex(2, 2, _selectedBg);
		_resizeHandleTex= MakeTex(1, 1, _resizeHandleColor);
		_resizeHoverTex = MakeTex(1, 1, _resizeHoverColor);
		_selectedBtnTex = MakeTex(2, 2, _selectedBg);
		_lineTex        = MakeTex(2, 2, _line);

		_boxStyle = new GUIStyle(GUI.skin.box);
		_boxStyle.normal.background = _bgTex;
		_boxStyle.normal.textColor = _textColor;

		_headerStyle = new GUIStyle(GUI.skin.label);
		_headerStyle.fontSize = 14;
		_headerStyle.fontStyle = FontStyle.Bold;
		_headerStyle.normal.textColor = _ink;
		_headerStyle.alignment = TextAnchor.MiddleLeft;

		_labelStyle = new GUIStyle(GUI.skin.label);
		_labelStyle.normal.textColor = _textColor;
		_labelStyle.fontSize = 13;
		_labelStyle.wordWrap = true;

		_labelSubStyle = new GUIStyle(_labelStyle);
		_labelSubStyle.fontSize = 11;
		_labelSubStyle.normal.textColor = _inkSoft;

		_fieldLabelStyle = new GUIStyle(_labelStyle);
		_fieldLabelStyle.fontSize = 11;
		_fieldLabelStyle.fontStyle = FontStyle.Bold;
		_fieldLabelStyle.normal.textColor = _inkSoft;

		_btnStyle = new GUIStyle(GUI.skin.button);
		_btnStyle.normal.background = _btnTex;
		_btnStyle.hover.background = _btnHoverTex;
		_btnStyle.active.background = _selectedBtnTex;
		_btnStyle.normal.textColor = _textColor;
		_btnStyle.hover.textColor = _selectedOrange;
		_btnStyle.active.textColor = _selectedOrange;
		_btnStyle.fontSize = 13;
		_btnStyle.fontStyle = FontStyle.Bold;
		_btnStyle.padding = new RectOffset(8, 8, 4, 4);

		_miniStyle = new GUIStyle(GUI.skin.button);
		_miniStyle.normal.background = null;
		_miniStyle.hover.background = _btnHoverTex;
		_miniStyle.active.background = _selectedBtnTex;
		_miniStyle.normal.textColor = _selectedOrange;
		_miniStyle.hover.textColor = _selectedOrange;
		_miniStyle.active.textColor = _selectedOrange;
		_miniStyle.fontSize = 16;
		_miniStyle.alignment = TextAnchor.MiddleCenter;
		_miniStyle.fontStyle = FontStyle.Bold;

		_sectionStyle = new GUIStyle(GUI.skin.box);
		_sectionStyle.normal.background = _sectionTex;
		_sectionStyle.normal.textColor = _textColor;
		_sectionStyle.padding = new RectOffset(8, 8, 6, 6);

		_toggleStyle = new GUIStyle(GUI.skin.toggle);
		_toggleStyle.normal.textColor = _textColor;
		_toggleStyle.hover.textColor = _selectedOrange;
		_toggleStyle.onNormal.textColor = _selectedOrange;
		_toggleStyle.onHover.textColor = _selectedOrange;
		_toggleStyle.fontSize = 13;

		_slotCellStyle = new GUIStyle(GUI.skin.button);
		_slotCellStyle.normal.background = _btnTex;
		_slotCellStyle.hover.background = _btnHoverTex;
		_slotCellStyle.active.background = _selectedBtnTex;
		_slotCellStyle.fontSize = 12;
		_slotCellStyle.alignment = TextAnchor.MiddleCenter;
		_slotCellStyle.normal.textColor = _textColor;
		_slotCellStyle.hover.textColor = _selectedOrange;
		_slotCellStyle.active.textColor = _selectedOrange;
		_slotCellStyle.margin = new RectOffset(1, 1, 1, 1);

		_tabStyle = new GUIStyle(GUI.skin.button);
		_tabStyle.normal.background = _btnTex;
		_tabStyle.hover.background = _btnHoverTex;
		_tabStyle.active.background = _selectedBtnTex;
		_tabStyle.fontSize = 13;
		_tabStyle.fontStyle = FontStyle.Bold;
		_tabStyle.normal.textColor = _textColor;
		_tabStyle.hover.textColor = _selectedOrange;
		_tabStyle.active.textColor = _selectedOrange;

		_chipStyle = new GUIStyle(GUI.skin.button);
		_chipStyle.normal.background = _btnTex;
		_chipStyle.hover.background = _btnHoverTex;
		_chipStyle.active.background = _selectedBtnTex;
		_chipStyle.fontSize = 12;
		_chipStyle.fontStyle = FontStyle.Bold;
		_chipStyle.normal.textColor = _textColor;
		_chipStyle.hover.textColor = _selectedOrange;
		_chipStyle.active.textColor = _selectedOrange;
		_chipStyle.padding = new RectOffset(10, 10, 4, 4);

		_resizeLabelStyle = new GUIStyle(GUI.skin.label);
		_resizeLabelStyle.fontSize = 20;
		_resizeLabelStyle.fontStyle = FontStyle.Bold;
		_resizeLabelStyle.alignment = TextAnchor.LowerRight;
		_resizeLabelStyle.normal.textColor = _resizeHandleColor;

		_resizeLabelHoverStyle = new GUIStyle(GUI.skin.label);
		_resizeLabelHoverStyle.fontSize = 20;
		_resizeLabelHoverStyle.fontStyle = FontStyle.Bold;
		_resizeLabelHoverStyle.alignment = TextAnchor.LowerRight;
		_resizeLabelHoverStyle.normal.textColor = _selectedOrange;

		_stylesReady = true;
	}

	private GUIStyle MakeSelectableBtnStyle(bool isSelected)
	{
		GUIStyle st = new GUIStyle(_btnStyle);
		if (isSelected)
		{
			st.normal.background = _selectedBtnTex;
			st.hover.background = _selectedBtnTex;
			st.active.background = _selectedBtnTex;
			st.normal.textColor = _selectedText;
			st.hover.textColor = _selectedText;
			st.active.textColor = _selectedText;
		}
		return st;
	}

	private void OnDestroy()
	{
		if ((UnityEngine.Object)(object)_uiRaycastShieldObj != (UnityEngine.Object)null)
		{
			UnityEngine.Object.Destroy((UnityEngine.Object)(object)_uiRaycastShieldObj);
			_uiRaycastShieldObj = null;
		}
		if ((UnityEngine.Object)(object)_uiTargetedRaycastShieldObj != (UnityEngine.Object)null)
		{
			UnityEngine.Object.Destroy((UnityEngine.Object)(object)_uiTargetedRaycastShieldObj);
			_uiTargetedRaycastShieldObj = null;
		}
		DestroyTexSafe(_runningBtnTex);
		DestroyTexSafe(_resizeHandleTex);
		DestroyTexSafe(_resizeHoverTex);
		DestroyTexSafe(_bgTex);
		DestroyTexSafe(_headerTex);
		DestroyTexSafe(_btnTex);
		DestroyTexSafe(_btnHoverTex);
		DestroyTexSafe(_sectionTex);
		DestroyTexSafe(_miniTex);
		DestroyTexSafe(_selectedBtnTex);
		DestroyTexSafe(_lineTex);
	}

	private void DestroyTexSafe(Texture2D tex)
	{
		if ((UnityEngine.Object)(object)tex != (UnityEngine.Object)null)
		{
			UnityEngine.Object.Destroy((UnityEngine.Object)(object)tex);
		}
	}

	private void OpenTargetedSubWindow()
	{
		if (!_targetedSubWindowPosInitialized && Screen.width > 200 && Screen.height > 200)
		{
			float candidateX = _windowRect.xMax + 10f;
			if (candidateX + _targetedWindowRect.width > (float)Screen.width - 8f)
			{
				candidateX = Mathf.Max(8f, _windowRect.x - _targetedWindowRect.width - 10f);
			}
			_targetedWindowRect.x = Mathf.Clamp(candidateX, 4f, Mathf.Max(4f, (float)Screen.width - _targetedWindowRect.width - 4f));
			_targetedWindowRect.y = Mathf.Clamp(_windowRect.y, 4f, Mathf.Max(4f, (float)Screen.height - _targetedWindowRect.height - 4f));
			_targetedSubWindowPosInitialized = true;
		}
		_showTargetedSubWindow = true;
	}

	private void OnGUI()
	{
		if (!_showWindow) return;

		if ((UnityEngine.Object)(object)_ctrl == (UnityEngine.Object)null)
		{
			_ctrl = ((Component)this).GetComponent<AutoController>();
			if ((UnityEngine.Object)(object)_ctrl == (UnityEngine.Object)null) return;
		}
		if ((UnityEngine.Object)(object)_bball == (UnityEngine.Object)null)
		{
			_bball = ((Component)this).GetComponent<BasketballController>();
		}

		if (!_initialScreenClampDone && Screen.width > 200 && Screen.height > 200)
		{
			_windowRect.width = Mathf.Clamp(_windowRect.width, MinWidth, Mathf.Max(MinWidth, (float)Screen.width - 30f));
			_windowRect.height = Mathf.Clamp(_windowRect.height, MinHeight, Mathf.Max(MinHeight, (float)Screen.height - 30f));
			_targetedWindowRect.width = Mathf.Clamp(_targetedWindowRect.width, MinWidth, Mathf.Max(MinWidth, (float)Screen.width - 30f));
			_targetedWindowRect.height = Mathf.Clamp(_targetedWindowRect.height, MinHeight, Mathf.Max(MinHeight, (float)Screen.height - 30f));
			_initialScreenClampDone = true;
		}

		InitStyles();
		Color color = GUI.color;
		GUI.color = new Color(1f, 1f, 1f, _opacity);

		if (_minimized)
		{
			_isResizingCorner = false;
			_isResizingTargetedCorner = false;
			DrawMini();
		}
		else
		{
			_miniPointerDown = false;
			_miniIsDragging = false;
			_miniPopupMode = MiniPopupMode.None;

			// 必须在 GUI.Window 之前优先拦截主窗口与专项子窗口的右下角拖拽事件
			HandleMainResizeInput();
			if (_showTargetedSubWindow)
			{
				HandleTargetedResizeInput();
			}
			else
			{
				_isResizingTargetedCorner = false;
				_targetedResizeHover = false;
			}

			_windowRect = GUI.Window(99001, _windowRect, new GUI.WindowFunction(DrawWindow), "", _boxStyle);
			if (_showTargetedSubWindow)
			{
				_targetedWindowRect = GUI.Window(99002, _targetedWindowRect, new GUI.WindowFunction(DrawTargetedSubWindow), "", _boxStyle);
			}
		}

		GUI.color = color;
		SyncIMEAndTextInputState();

		if ((_editingPresetOldName != null || _creatingPreset || _targetedPresetDropdownOpen || _editingBasicPresetOldName != null || _creatingBasicPreset || _basicPresetDropdownOpen || _statusBasicDropdownOpen || _statusTargetedDropdownOpen) && Event.current.type == EventType.MouseDown)
		{
			Vector2 mousePosition = Event.current.mousePosition;
			bool insideMain = !_minimized && _windowRect.Contains(mousePosition);
			bool insideTargeted = !_minimized && _showTargetedSubWindow && _targetedWindowRect.Contains(mousePosition);
			if (!insideMain && !insideTargeted)
			{
				_editingPresetOldName = null;
				_creatingPreset = false;
				_targetedPresetDropdownOpen = false;
				_editingBasicPresetOldName = null;
				_creatingBasicPreset = false;
				_basicPresetDropdownOpen = false;
				_statusBasicDropdownOpen = false;
				_statusTargetedDropdownOpen = false;
			}
		}
	}

	/// <summary>缩小后的完整一体化状态栏标签：
	/// 最左侧 ▼ 展开窗口（严格以左上角为锚点对齐 _windowRect.x/_windowRect.y），
	/// 其后依次为：自动/金玲运行状态（单击启停自动）、基础预设快捷切换、专项总开关、专项预设快捷切换、一键寻路：出口/下一层。</summary>
	private void DrawMini()
	{
		float totalWidth = MiniTotalWidth;
		float boxHeight = MiniBoxHeight;

		if (_miniPosX < 0f || _miniPosY < 0f)
		{
			// 以左上角为点对齐
			_miniPosX = Mathf.Clamp(_windowRect.x, 4f, Mathf.Max(4f, (float)Screen.width - totalWidth - 4f));
			_miniPosY = Mathf.Clamp(_windowRect.y, 4f, Mathf.Max(4f, (float)Screen.height - boxHeight - 4f));
		}

		_miniPosX = Mathf.Clamp(_miniPosX, 4f, Mathf.Max(4f, (float)Screen.width - totalWidth - 4f));
		_miniPosY = Mathf.Clamp(_miniPosY, 4f, Mathf.Max(4f, (float)Screen.height - boxHeight - 4f));

		Rect totalRect = new Rect(_miniPosX, _miniPosY, totalWidth, boxHeight);
		Rect expandRect = new Rect(_miniPosX, _miniPosY, MiniExpandWidth, boxHeight);
		Rect statusRect = new Rect(expandRect.xMax, _miniPosY, MiniStatusWidth, boxHeight);
		Rect basicPresetRect = new Rect(statusRect.xMax, _miniPosY, MiniBasicPresetWidth, boxHeight);
		Rect targetToggleRect = new Rect(basicPresetRect.xMax, _miniPosY, MiniTargetToggleWidth, boxHeight);
		Rect targetPresetRect = new Rect(targetToggleRect.xMax, _miniPosY, MiniTargetPresetWidth, boxHeight);
		Rect dungeonNavRect = new Rect(targetPresetRect.xMax, _miniPosY, MiniDungeonNavWidth, boxHeight);

		// 处理左键拖拽（按住标签任意位置移动超过 3px 即进入拖拽，松开时不误触按钮）
		Event current = Event.current;
		if (current != null)
		{
			if (current.type == EventType.MouseDown && current.button == 0)
			{
				if (totalRect.Contains(current.mousePosition))
				{
					_miniPointerDown = true;
					_miniIsDragging = false;
					_miniMouseDownPos = current.mousePosition;
					_miniDragStartPos = new Vector2(_miniPosX, _miniPosY);
				}
				else if (_miniPopupMode != MiniPopupMode.None)
				{
					Rect popRect = GetMiniPopupRect(_miniPosX, _miniPosY, totalWidth, boxHeight);
					if (!popRect.Contains(current.mousePosition))
					{
						_miniPopupMode = MiniPopupMode.None;
					}
				}
			}
			else if (current.type == EventType.MouseDrag && current.button == 0 && _miniPointerDown)
			{
				Vector2 delta = current.mousePosition - _miniMouseDownPos;
				if (_miniIsDragging || delta.sqrMagnitude > 9f)
				{
					_miniIsDragging = true;
					_miniPopupMode = MiniPopupMode.None;
					_miniPosX = Mathf.Clamp(_miniDragStartPos.x + delta.x, 4f, Mathf.Max(4f, (float)Screen.width - totalWidth - 4f));
					_miniPosY = Mathf.Clamp(_miniDragStartPos.y + delta.y, 4f, Mathf.Max(4f, (float)Screen.height - boxHeight - 4f));
					totalRect = new Rect(_miniPosX, _miniPosY, totalWidth, boxHeight);
					expandRect = new Rect(_miniPosX, _miniPosY, MiniExpandWidth, boxHeight);
					statusRect = new Rect(expandRect.xMax, _miniPosY, MiniStatusWidth, boxHeight);
					basicPresetRect = new Rect(statusRect.xMax, _miniPosY, MiniBasicPresetWidth, boxHeight);
					targetToggleRect = new Rect(basicPresetRect.xMax, _miniPosY, MiniTargetToggleWidth, boxHeight);
					targetPresetRect = new Rect(targetToggleRect.xMax, _miniPosY, MiniTargetPresetWidth, boxHeight);
					dungeonNavRect = new Rect(targetPresetRect.xMax, _miniPosY, MiniDungeonNavWidth, boxHeight);
					current.Use();
				}
			}
			else if (current.type == EventType.MouseUp && current.button == 0 && _miniPointerDown)
			{
				_miniPointerDown = false;
				if (_miniIsDragging)
				{
					_miniIsDragging = false;
					current.Use();
				}
			}
		}

		bool autoOn = (UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null && _ctrl.Enabled;
		bool navOn = (UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null && _ctrl.IsDungeonNavigating;
		string autoLine = "○ 自动: 已停止";
		if (navOn)
		{
			string navTargetText = (_ctrl.CurrentDungeonNav == AutoController.DungeonNavTarget.Exit) ? "出口" : "下一层";
			int stateIdx = (int)_ctrl.CurrentState;
			string subState = (stateIdx > 0 && stateIdx < _stateNames.Length) ? ("·" + _stateNames[stateIdx]) : "";
			autoLine = "● 寻路: " + navTargetText + subState;
		}
		else if (autoOn)
		{
			int stateIdx = (int)_ctrl.CurrentState;
			string subState = (stateIdx >= 0 && stateIdx < _stateNames.Length) ? _stateNames[stateIdx] : "运行中";
			autoLine = "● 自动: " + subState;
		}

		string bballLine = "○ 金玲: 已停止";
		bool bballActive = false;
		if ((UnityEngine.Object)(object)_bball != (UnityEngine.Object)null)
		{
			if (_bball.Enabled)
			{
				bballActive = true;
				string bballSub = (_bball.CurrentState == BasketballController.RunState.Holding) ? "投篮按住" : ((_bball.CurrentState == BasketballController.RunState.Waiting) ? "投篮间隔" : "投篮中");
				bballLine = "● 金玲: " + bballSub;
			}
			else if (_bball.LeverRunning)
			{
				bballActive = true;
				bballLine = "● 金玲: 拉杆执行中";
			}
		}

		bool anyRunning = autoOn || navOn || bballActive;
		bool targetedOn = (UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null && _ctrl.TargetedGatherEnabled;
		int targetedCount = ((UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null) ? _ctrl.TargetedGatherKeys.Count : 0;

		// 一体化完整标签外框与底色
		GUI.DrawTexture(new Rect(totalRect.x - 1f, totalRect.y - 1f, totalRect.width + 2f, totalRect.height + 2f), (Texture)(object)_headerTex);
		GUI.DrawTexture(totalRect, (Texture)(object)(anyRunning ? _selectedBtnTex : _bgTex));
		if (_miniPopupMode == MiniPopupMode.BasicPreset)
		{
			GUI.DrawTexture(basicPresetRect, (Texture)(object)_selectedBtnTex);
		}
		if (targetedOn)
		{
			GUI.DrawTexture(targetToggleRect, (Texture)(object)_selectedBtnTex);
		}
		if (_miniPopupMode == MiniPopupMode.TargetedPreset)
		{
			GUI.DrawTexture(targetPresetRect, (Texture)(object)_selectedBtnTex);
		}
		if (navOn)
		{
			GUI.DrawTexture(dungeonNavRect, (Texture)(object)_selectedBtnTex);
		}

		// 内部细分割线
		GUI.DrawTexture(new Rect(expandRect.xMax, totalRect.y + 4f, 1f, boxHeight - 8f), (Texture)(object)_headerTex);
		GUI.DrawTexture(new Rect(statusRect.xMax, totalRect.y + 4f, 1f, boxHeight - 8f), (Texture)(object)_headerTex);
		GUI.DrawTexture(new Rect(basicPresetRect.xMax, totalRect.y + 4f, 1f, boxHeight - 8f), (Texture)(object)_headerTex);
		GUI.DrawTexture(new Rect(targetToggleRect.xMax, totalRect.y + 4f, 1f, boxHeight - 8f), (Texture)(object)_headerTex);
		GUI.DrawTexture(new Rect(targetPresetRect.xMax, totalRect.y + 4f, 1f, boxHeight - 8f), (Texture)(object)_headerTex);

		// 1) 最左侧：展开窗口箭头 ▼（严格以左上角为锚点还原窗口坐标）
		if (GUI.Button(expandRect, "▼", _miniStyle))
		{
			_windowRect.x = Mathf.Clamp(_miniPosX, 4f, Mathf.Max(4f, (float)Screen.width - _windowRect.width - 4f));
			_windowRect.y = Mathf.Clamp(_miniPosY, 4f, Mathf.Max(4f, (float)Screen.height - _windowRect.height - 4f));
			_minimized = false;
			_miniPopupMode = MiniPopupMode.None;
		}

		// 2) 自动/金玲运行状态（点击启停自动或停止地牢寻路）
		GUIStyle miniStatusBtnStyle = new GUIStyle(_btnStyle);
		miniStatusBtnStyle.normal.background = null;
		miniStatusBtnStyle.hover.background = _btnHoverTex;
		miniStatusBtnStyle.active.background = _selectedBtnTex;
		miniStatusBtnStyle.normal.textColor = anyRunning ? _selectedOrange : _textColor;
		miniStatusBtnStyle.hover.textColor = _selectedOrange;
		miniStatusBtnStyle.active.textColor = _selectedOrange;
		miniStatusBtnStyle.fontSize = 11;
		miniStatusBtnStyle.fontStyle = FontStyle.Bold;
		miniStatusBtnStyle.alignment = TextAnchor.MiddleLeft;
		miniStatusBtnStyle.padding = new RectOffset(6, 4, 3, 3);

		if (GUI.Button(statusRect, autoLine + "\n" + bballLine, miniStatusBtnStyle))
		{
			if ((UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null)
			{
				if (_ctrl.IsDungeonNavigating && !_ctrl.Enabled)
				{
					_ctrl.StopDungeonNav(cancelPCAct: true);
				}
				else
				{
					_ctrl.ToggleEnabled();
				}
			}
		}

		// 3) 基础设置预设快捷切换按钮
		GUIStyle miniCellBtnStyle = new GUIStyle(_btnStyle);
		miniCellBtnStyle.normal.background = null;
		miniCellBtnStyle.hover.background = _btnHoverTex;
		miniCellBtnStyle.active.background = _selectedBtnTex;
		miniCellBtnStyle.normal.textColor = (_miniPopupMode == MiniPopupMode.BasicPreset) ? _selectedOrange : _textColor;
		miniCellBtnStyle.hover.textColor = _selectedOrange;
		miniCellBtnStyle.active.textColor = _selectedOrange;
		miniCellBtnStyle.fontSize = 11;
		miniCellBtnStyle.fontStyle = FontStyle.Bold;
		miniCellBtnStyle.alignment = TextAnchor.MiddleCenter;
		miniCellBtnStyle.padding = new RectOffset(2, 2, 3, 3);

		string basicName = ((UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null) ? TruncateShort(_ctrl.ActiveBasicPresetName, 6) : "默认";
		string basicMark = (_ctrl != null && _ctrl.IsBasicPresetDirty) ? "*" : "";
		string basicArrow = (_miniPopupMode == MiniPopupMode.BasicPreset) ? "▲" : "▾";
		if (GUI.Button(basicPresetRect, $"⚙基础预设{basicArrow}\n{basicName}{basicMark}", miniCellBtnStyle))
		{
			_miniPopupMode = (_miniPopupMode == MiniPopupMode.BasicPreset) ? MiniPopupMode.None : MiniPopupMode.BasicPreset;
		}

		// 4) 专项收集总开关
		GUIStyle miniTargetBtnStyle = new GUIStyle(miniCellBtnStyle);
		miniTargetBtnStyle.normal.textColor = targetedOn ? _selectedOrange : _inkSoft;
		string targetText = targetedOn ? $"🎯专项:开\n({targetedCount}项)" : "🎯专项:关\n(默认全采)";
		if (GUI.Button(targetToggleRect, targetText, miniTargetBtnStyle))
		{
			if ((UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null)
			{
				_ctrl.TargetedGatherEnabled = !_ctrl.TargetedGatherEnabled;
				_ctrl.SaveConfig();
			}
		}

		// 5) 专项预设快捷切换按钮
		GUIStyle miniTargetPresetStyle = new GUIStyle(miniCellBtnStyle);
		miniTargetPresetStyle.normal.textColor = (_miniPopupMode == MiniPopupMode.TargetedPreset || targetedOn) ? _selectedOrange : _textColor;
		string tPresetName = ((UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null) ? TruncateShort(_ctrl.ActivePresetName, 6) : "默认";
		string tPresetMark = (_ctrl != null && _ctrl.IsTargetedPresetDirty) ? "*" : "";
		string tPresetArrow = (_miniPopupMode == MiniPopupMode.TargetedPreset) ? "▲" : "▾";
		if (GUI.Button(targetPresetRect, $"📋专项预设{tPresetArrow}\n{tPresetName}{tPresetMark}", miniTargetPresetStyle))
		{
			_miniPopupMode = (_miniPopupMode == MiniPopupMode.TargetedPreset) ? MiniPopupMode.None : MiniPopupMode.TargetedPreset;
		}

		// 6) 地牢一键寻路：出口 / 下一层（仅显示在缩小后的状态框上）
		GUIStyle miniNavTitleStyle = new GUIStyle(_labelStyle);
		miniNavTitleStyle.fontSize = 11;
		miniNavTitleStyle.fontStyle = FontStyle.Bold;
		miniNavTitleStyle.alignment = TextAnchor.MiddleCenter;
		miniNavTitleStyle.normal.textColor = navOn ? _selectedOrange : _textColor;
		GUI.Label(new Rect(dungeonNavRect.x + 2f, _miniPosY + 2f, MiniDungeonNavWidth - 4f, 16f), "一键寻路:", miniNavTitleStyle);

		bool isNavExit = (UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null && _ctrl.CurrentDungeonNav == AutoController.DungeonNavTarget.Exit;
		bool isNavNext = (UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null && _ctrl.CurrentDungeonNav == AutoController.DungeonNavTarget.NextFloor;

		GUIStyle navExitStyle = MakeSelectableBtnStyle(isNavExit);
		navExitStyle.fontSize = 11;
		navExitStyle.padding = new RectOffset(2, 2, 1, 1);

		GUIStyle navNextStyle = MakeSelectableBtnStyle(isNavNext);
		navNextStyle.fontSize = 11;
		navNextStyle.padding = new RectOffset(2, 2, 1, 1);

		Rect navExitBtnRect = new Rect(dungeonNavRect.x + 4f, _miniPosY + 19f, 46f, 20f);
		Rect navNextBtnRect = new Rect(dungeonNavRect.x + 53f, _miniPosY + 19f, 51f, 20f);

		if (GUI.Button(navExitBtnRect, "出口", navExitStyle))
		{
			if ((UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null)
			{
				_ctrl.ToggleDungeonNav(AutoController.DungeonNavTarget.Exit);
			}
		}
		if (GUI.Button(navNextBtnRect, "下一层", navNextStyle))
		{
			if ((UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null)
			{
				_ctrl.ToggleDungeonNav(AutoController.DungeonNavTarget.NextFloor);
			}
		}

		// 金玲快捷开关（独立于采集脚本，一键启停投篮）
		Rect bballMiniRect = new Rect(dungeonNavRect.xMax, _miniPosY, MiniBballWidth, boxHeight);
		GUI.DrawTexture(new Rect(bballMiniRect.x, _miniPosY + 6f, 1f, boxHeight - 12f), (Texture)(object)_lineTex);
		bool bballOn = (UnityEngine.Object)(object)_bball != (UnityEngine.Object)null && _bball.Enabled;
		GUIStyle bballMiniStyle = MakeSelectableBtnStyle(bballOn);
		bballMiniStyle.fontSize = 11;
		bballMiniStyle.padding = new RectOffset(2, 2, 1, 1);
		if (GUI.Button(new Rect(bballMiniRect.x + 4f, _miniPosY + 6f, MiniBballWidth - 8f, boxHeight - 12f), bballOn ? "🏀开" : "🏀金玲", bballMiniStyle))
		{
			if ((UnityEngine.Object)(object)_bball != (UnityEngine.Object)null)
			{
				_bball.ToggleEnabled();
			}
		}

		// 绘制缩小状态栏下方的快捷预设切换弹出框
		if (_miniPopupMode != MiniPopupMode.None && (UnityEngine.Object)(object)_ctrl != (UnityEngine.Object)null)
		{
			DrawMiniPresetPopup(_miniPosX, _miniPosY, totalWidth, boxHeight);
		}
	}

	private static string TruncateShort(string s, int maxChars)
	{
		if (string.IsNullOrEmpty(s)) return "";
		return (s.Length <= maxChars) ? s : (s.Substring(0, maxChars) + "…");
	}

	private void DrawMiniPresetPopup(float miniX, float miniY, float totalWidth, float boxHeight)
	{
		Rect popRect = GetMiniPopupRect(miniX, miniY, totalWidth, boxHeight);
		GUI.DrawTexture(new Rect(popRect.x - 1f, popRect.y - 1f, popRect.width + 2f, popRect.height + 2f), (Texture)(object)_headerTex);
		GUI.DrawTexture(popRect, (Texture)(object)_bgTex);

		GUILayout.BeginArea(new Rect(popRect.x + 4f, popRect.y + 4f, popRect.width - 8f, popRect.height - 8f));
		bool isBasic = _miniPopupMode == MiniPopupMode.BasicPreset;
		GUILayout.BeginHorizontal();
		GUILayout.Label(isBasic ? "切换【基础设置】预设：" : "切换【专项收集】预设：", _fieldLabelStyle);
		GUILayout.FlexibleSpace();
		if (!isBasic && GUILayout.Button("打开专项窗", _btnStyle, GUILayout.Width(76f), GUILayout.Height(20f)))
		{
			_windowRect.x = Mathf.Clamp(_miniPosX, 4f, Mathf.Max(4f, (float)Screen.width - _windowRect.width - 4f));
			_windowRect.y = Mathf.Clamp(_miniPosY, 4f, Mathf.Max(4f, (float)Screen.height - _windowRect.height - 4f));
			_minimized = false;
			_miniPopupMode = MiniPopupMode.None;
			OpenTargetedSubWindow();
		}
		if (GUILayout.Button("×", _btnStyle, GUILayout.Width(22f), GUILayout.Height(20f)))
		{
			_miniPopupMode = MiniPopupMode.None;
		}
		GUILayout.EndHorizontal();

		List<string> presetNames = isBasic ? _ctrl.GetBasicPresetNames() : _ctrl.GetPresetNames();
		string activeName = isBasic ? _ctrl.ActiveBasicPresetName : _ctrl.ActivePresetName;

		_miniPopupScroll = GUILayout.BeginScrollView(_miniPopupScroll, GUILayout.Height(popRect.height - 32f));
		for (int i = 0; i < presetNames.Count; i++)
		{
			string pName = presetNames[i];
			bool isCurrent = string.Equals(pName, activeName, StringComparison.OrdinalIgnoreCase);
			GUIStyle itemStyle = new GUIStyle(_btnStyle);
			itemStyle.alignment = TextAnchor.MiddleLeft;
			itemStyle.fontSize = 12;
			itemStyle.padding = new RectOffset(8, 8, 2, 2);
			if (isCurrent)
			{
				itemStyle.normal.background = _selectedBtnTex;
				itemStyle.hover.background = _selectedBtnTex;
				itemStyle.normal.textColor = _selectedOrange;
				itemStyle.hover.textColor = _selectedOrange;
			}
			string info = isBasic
				? $" ({_ctrl.GetBasicPresetSummary(pName)})"
				: $" ({_ctrl.GetPresetSavedItemCount(pName)}项)";
			string rowText = FormatNumberedPresetTitle(i, pName) + info + (isCurrent ? " ✓" : "");
			if (GUILayout.Button(rowText, itemStyle, GUILayout.ExpandWidth(true), GUILayout.Height(24f)))
			{
				if (isBasic)
				{
					_ctrl.SwitchToBasicPreset(pName);
				}
				else
				{
					_ctrl.SwitchToPreset(pName);
				}
				_miniPopupMode = MiniPopupMode.None;
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	/// <summary>获取任意窗口右下角缩放热区（含窗口内部 46×32 区域 + 向窗口外右下侧延伸 14px 容差）。</summary>
	private Rect GetResizeHandleRect(Rect winRect)
	{
		return new Rect(
			winRect.xMax - ResizeGripWidth,
			winRect.yMax - ResizeGripHeight,
			ResizeGripWidth + ResizeOuterMargin,
			ResizeGripHeight + ResizeOuterMargin);
	}

	/// <summary>在 GUI.Window 之前处理主窗口右下角拖拽缩放输入（左、上边界固定，仅向右和向下拉伸）。</summary>
	private void HandleMainResizeInput()
	{
		Event current = Event.current;
		if (current == null) return;

		Vector2 screenMouse = new Vector2(Input.mousePosition.x, (float)Screen.height - Input.mousePosition.y);
		Vector2 mousePosition = current.mousePosition;
		Rect handleRect = GetResizeHandleRect(_windowRect);
		_resizeHover = _isResizingCorner || handleRect.Contains(mousePosition) || handleRect.Contains(screenMouse);

		if (!_isResizingCorner && !_isResizingTargetedCorner && current.type == EventType.MouseDown && current.button == 0 && (handleRect.Contains(mousePosition) || handleRect.Contains(screenMouse)))
		{
			_isResizingCorner = true;
			_resizeGrabOffset = new Vector2(_windowRect.xMax - screenMouse.x, _windowRect.yMax - screenMouse.y);
			GUIUtility.hotControl = 0;
			GUIUtility.keyboardControl = 0;
			current.Use();
			return;
		}

		if (_isResizingCorner)
		{
			if ((current.type == EventType.MouseUp && current.button == 0) || !Input.GetMouseButton(0))
			{
				_isResizingCorner = false;
				if (current.type == EventType.MouseUp && current.button == 0)
				{
					current.Use();
				}
				return;
			}

			float maxAllowedW = Mathf.Max(MinWidth, Mathf.Min(MaxWidth, (float)Screen.width - _windowRect.x - 4f));
			float maxAllowedH = Mathf.Max(MinHeight, Mathf.Min(MaxHeight, (float)Screen.height - _windowRect.y - 4f));
			float targetMaxX = screenMouse.x + _resizeGrabOffset.x;
			float targetMaxY = screenMouse.y + _resizeGrabOffset.y;
			_windowRect.width = Mathf.Clamp(targetMaxX - _windowRect.x, MinWidth, maxAllowedW);
			_windowRect.height = Mathf.Clamp(targetMaxY - _windowRect.y, MinHeight, maxAllowedH);

			if (current.type == EventType.MouseDrag || current.type == EventType.MouseDown || current.type == EventType.MouseMove)
			{
				current.Use();
			}
		}
	}

	/// <summary>在 GUI.Window 之前处理专项子窗口右下角拖拽缩放输入。</summary>
	private void HandleTargetedResizeInput()
	{
		Event current = Event.current;
		if (current == null) return;

		Vector2 screenMouse = new Vector2(Input.mousePosition.x, (float)Screen.height - Input.mousePosition.y);
		Vector2 mousePosition = current.mousePosition;
		Rect handleRect = GetResizeHandleRect(_targetedWindowRect);
		_targetedResizeHover = _isResizingTargetedCorner || handleRect.Contains(mousePosition) || handleRect.Contains(screenMouse);

		if (!_isResizingTargetedCorner && !_isResizingCorner && current.type == EventType.MouseDown && current.button == 0 && (handleRect.Contains(mousePosition) || handleRect.Contains(screenMouse)))
		{
			_isResizingTargetedCorner = true;
			_targetedResizeGrabOffset = new Vector2(_targetedWindowRect.xMax - screenMouse.x, _targetedWindowRect.yMax - screenMouse.y);
			GUIUtility.hotControl = 0;
			GUIUtility.keyboardControl = 0;
			current.Use();
			return;
		}

		if (_isResizingTargetedCorner)
		{
			if ((current.type == EventType.MouseUp && current.button == 0) || !Input.GetMouseButton(0))
			{
				_isResizingTargetedCorner = false;
				if (current.type == EventType.MouseUp && current.button == 0)
				{
					current.Use();
				}
				return;
			}

			float maxAllowedW = Mathf.Max(MinWidth, Mathf.Min(MaxWidth, (float)Screen.width - _targetedWindowRect.x - 4f));
			float maxAllowedH = Mathf.Max(MinHeight, Mathf.Min(MaxHeight, (float)Screen.height - _targetedWindowRect.y - 4f));
			float targetMaxX = screenMouse.x + _targetedResizeGrabOffset.x;
			float targetMaxY = screenMouse.y + _targetedResizeGrabOffset.y;
			_targetedWindowRect.width = Mathf.Clamp(targetMaxX - _targetedWindowRect.x, MinWidth, maxAllowedW);
			_targetedWindowRect.height = Mathf.Clamp(targetMaxY - _targetedWindowRect.y, MinHeight, maxAllowedH);

			if (current.type == EventType.MouseDrag || current.type == EventType.MouseDown || current.type == EventType.MouseMove)
			{
				current.Use();
			}
		}
	}

	/// <summary>在窗口内部最上层绘制加大后的右下角拖拽手柄与高亮反馈块。</summary>
	private void DrawResizeGrip(float width, float height, bool isHoverOrResizing)
	{
		Rect gripBox = new Rect(width - ResizeGripWidth + 2f, height - ResizeGripHeight + 2f, ResizeGripWidth - 4f, ResizeGripHeight - 4f);
		if (isHoverOrResizing)
		{
			GUI.DrawTexture(new Rect(gripBox.x - 1f, gripBox.y - 1f, gripBox.width + 2f, gripBox.height + 2f), (Texture)(object)_headerTex);
			GUI.DrawTexture(gripBox, (Texture)(object)_selectedBtnTex);
		}
		else
		{
			GUI.DrawTexture(gripBox, (Texture)(object)_sectionTex);
		}

		Texture2D tex = isHoverOrResizing ? _resizeHoverTex : _resizeHandleTex;
		GUI.DrawTexture(new Rect(width - 30f, height - 5f, 25f, 3f), (Texture)(object)tex);
		GUI.DrawTexture(new Rect(width - 5f, height - 26f, 3f, 21f), (Texture)(object)tex);
		GUI.DrawTexture(new Rect(width - 22f, height - 9f, 13f, 2f), (Texture)(object)tex);
		GUI.DrawTexture(new Rect(width - 9f, height - 20f, 2f, 11f), (Texture)(object)tex);

		GUIStyle labelStyle = isHoverOrResizing ? _resizeLabelHoverStyle : _resizeLabelStyle;
		GUI.Label(new Rect(width - 42f, height - 30f, 36f, 26f), "◢", labelStyle);
	}

	private void DrawWindow(int id)
	{
		float width = _windowRect.width;
		float contentWidth = width - 24f;

		// ---------- titlebar（缩小箭头 ▲ 移至最左侧，以左上角为锚点收起/展开，避免缩放窗口后位置偏移） ----------
		GUI.DrawTexture(new Rect(0f, 0f, width, 34f), (Texture)(object)_headerTex);

		GUIStyle iconBtnStyle = new GUIStyle(_btnStyle);
		iconBtnStyle.fontSize = 14;
		iconBtnStyle.normal.background = null;
		iconBtnStyle.normal.textColor = _ink;
		iconBtnStyle.hover.background = null;
		iconBtnStyle.hover.textColor = _selectedOrange;

		// 左上角缩小按钮 ▲：点击后严格以当前窗口左上角 (_windowRect.x, _windowRect.y) 作为最小化状态框的左上角
		if (GUI.Button(new Rect(6f, 5f, 26f, 24f), "▲", iconBtnStyle))
		{
			_miniPosX = Mathf.Clamp(_windowRect.x, 4f, Mathf.Max(4f, (float)Screen.width - MiniTotalWidth - 4f));
			_miniPosY = Mathf.Clamp(_windowRect.y, 4f, Mathf.Max(4f, (float)Screen.height - MiniBoxHeight - 4f));
			_minimized = true;
			_miniPopupMode = MiniPopupMode.None;
		}

		GUI.Label(new Rect(36f, 8f, width - 70f, 22f), "\ud83c\udf70 Elin 自动化", _headerStyle);

		if (GUI.Button(new Rect(width - 28f, 5f, 24f, 24f), "×", iconBtnStyle))
		{
			_showWindow = false;
		}

		GUILayout.Space(38f);
		// 底部留出 36px 余量给加大后的右下角缩放手柄，彻底避免 ScrollView 垂直滚动条与右下角拖拽区重叠
		_scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(Mathf.Max(120f, _windowRect.height - 74f)));
		GUILayout.BeginVertical();

		// ---------- run toggle + status strip（含基础预设与专项预设快捷切换） ----------
		DrawStatusStrip(contentWidth);

		GUILayout.Space(6f);
		DrawSeparator();
		GUILayout.Space(6f);

		// ---------- 顶部主导航栏（高频在前、低频在后，专项默认单窗内嵌，右上角仍支持弹出独立子窗） ----------
		GUILayout.BeginHorizontal();
		DrawTabButton(SettingsTab.Gather, "🌿 采集");
		DrawTabButton(SettingsTab.Targeted, $"🎯 专项({_ctrl.TargetedGatherKeys.Count})");
		DrawTabButton(SettingsTab.Combat, "⚔️ 战斗");
		DrawTabButton(SettingsTab.Config, "⚙️ 配置");
		DrawTabButton(SettingsTab.Basketball, "🏀 金玲");
		GUILayout.EndHorizontal();
		GUILayout.Space(6f);

		GUILayout.BeginVertical(_sectionStyle);
		switch (_activeTab)
		{
			case SettingsTab.Gather:
				DrawGatherTab(contentWidth);
				break;
			case SettingsTab.Targeted:
				DrawTargetedTab(contentWidth);
				break;
			case SettingsTab.Combat:
				DrawCombatTab(contentWidth);
				break;
			case SettingsTab.Config:
				DrawConfigTab(contentWidth);
				break;
			case SettingsTab.Basketball:
				DrawBasketballTab(contentWidth);
				break;
		}
		GUILayout.EndVertical();

		GUILayout.Space(6f);
		GUILayout.BeginVertical(_sectionStyle);
		GUILayout.Label($"窗口透明度: {_opacity:P0}", _labelStyle);
		_opacity = GUILayout.HorizontalSlider(_opacity, 0.3f, 1f);
		GUILayout.EndVertical();

		GUILayout.Space(4f);
		GUILayout.Label("F12 开关窗口 | Alt+Q 启停自动 | Alt+B 启停投篮 | Alt+A 拉杆 | 左上角 ▲ 最小化 | 右下角 ◢ 缩放", _labelSubStyle);

		GUILayout.EndVertical();
		GUILayout.EndScrollView();

		DrawResizeGrip(width, _windowRect.height, _resizeHover || _isResizingCorner);
		if (!_isResizingCorner)
		{
			// 避开左上角 ▲ 按钮与右上角 × 按钮
			GUI.DragWindow(new Rect(34f, 0f, Mathf.Max(40f, width - 66f), 34f));
		}
	}

	/// <summary>专项收集独立子窗口：可独立开关、自由拖拽标题栏、右下角缩放，与主窗口并排或叠放使用。</summary>
	private void DrawTargetedSubWindow(int id)
	{
		float width = _targetedWindowRect.width;
		float contentWidth = width - 24f;

		GUI.DrawTexture(new Rect(0f, 0f, width, 34f), (Texture)(object)_headerTex);
		GUI.Label(new Rect(12f, 8f, width - 50f, 22f), $"🎯 专项材料收集（当前预设: {_ctrl.ActivePresetName}）", _headerStyle);

		GUIStyle iconBtnStyle = new GUIStyle(_btnStyle);
		iconBtnStyle.fontSize = 14;
		iconBtnStyle.normal.background = null;
		iconBtnStyle.normal.textColor = _selectedBg;
		iconBtnStyle.hover.background = null;
		iconBtnStyle.hover.textColor = _selectedOrange;

		if (GUI.Button(new Rect(width - 28f, 5f, 24f, 24f), "×", iconBtnStyle))
		{
			_showTargetedSubWindow = false;
		}

		GUILayout.Space(38f);
		_targetedWindowScroll = GUILayout.BeginScrollView(_targetedWindowScroll, GUILayout.Height(Mathf.Max(120f, _targetedWindowRect.height - 74f)));
		GUILayout.BeginVertical(_sectionStyle);
		DrawTargetedTab(contentWidth);
		GUILayout.EndVertical();
		GUILayout.EndScrollView();

		DrawResizeGrip(width, _targetedWindowRect.height, _targetedResizeHover || _isResizingTargetedCorner);
		if (!_isResizingTargetedCorner)
		{
			GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(40f, width - 32f), 34f));
		}
	}

	private void DrawStatusStrip(float contentWidth)
	{
		GUILayout.BeginHorizontal();
		string runLabel = _ctrl.Enabled ? "● 运行中" : "○ 已停止";
		GUIStyle runStyle = MakeSelectableBtnStyle(_ctrl.Enabled);
		if (GUILayout.Button(runLabel, runStyle, GUILayout.Height(30f), GUILayout.Width(96f)))
		{
			_ctrl.ToggleEnabled();
		}
		GUILayout.Space(8f);
		GUILayout.BeginVertical();
		if (EClass.pc != null)
		{
			float hpPct = (float)((Card)EClass.pc).hp / (float)((Card)EClass.pc).MaxHP;
			int burdenPct = _ctrl.GetCurrentBurdenPercent(EClass.pc);
			int oxyPct = _ctrl.GetCurrentOxygenPercent(EClass.pc);
			string oxyPart = (oxyPct < 100 || _ctrl.SwimModeEnabled) ? $" · \ud83e\udee7{oxyPct}%" : "";
			GUILayout.Label($"♥ {hpPct:P0} · ★{EClass.pc.stamina.value} · ⚖{burdenPct}%{oxyPart} · \ud83d\udccd({((Card)EClass.pc).pos.x},{((Card)EClass.pc).pos.z})", _labelSubStyle);
			Rect barRect = GUILayoutUtility.GetRect(10f, 6f, GUILayout.ExpandWidth(true));
			DrawHpBar(barRect, hpPct);
		}
		GUILayout.EndVertical();
		GUILayout.EndHorizontal();

		int stateIdx = (int)_ctrl.CurrentState;
		string stateName = (stateIdx >= 0 && stateIdx < _stateNames.Length) ? _stateNames[stateIdx] : "未知状态";
		string envBadge = (EClass._zone != null) ? (_ctrl.IsInDungeonContext() ? "🏰地牢环境" : "🌿野外/家园") : "未进图";

		GUILayout.BeginHorizontal();
		GUILayout.Label($"当前状态: {stateName} ({envBadge})", _labelStyle);
		GUILayout.FlexibleSpace();
		bool nextTargeted = GUILayout.Toggle(_ctrl.TargetedGatherEnabled, $" 🎯专项收集({_ctrl.TargetedGatherKeys.Count}项)", _toggleStyle);
		if (nextTargeted != _ctrl.TargetedGatherEnabled)
		{
			_ctrl.TargetedGatherEnabled = nextTargeted;
			_ctrl.SaveConfig();
		}
		if (GUILayout.Button(_showTargetedSubWindow ? "收起专项窗" : "专项子窗口↗", MakeSelectableBtnStyle(_showTargetedSubWindow), GUILayout.Width(84f), GUILayout.Height(22f)))
		{
			if (_showTargetedSubWindow)
			{
				_showTargetedSubWindow = false;
			}
			else
			{
				OpenTargetedSubWindow();
			}
		}
		GUILayout.EndHorizontal();

		// ---------- 状态栏快捷预设切换行（支持一键拉取切换【基础预设】与【专项预设】） ----------
		GUILayout.Space(3f);
		GUILayout.BeginHorizontal();
		string bDirty = _ctrl.IsBasicPresetDirty ? "*" : "";
		string bBtnText = $"⚙ 基础预设: {_ctrl.ActiveBasicPresetName}{bDirty} " + (_statusBasicDropdownOpen ? "▲" : "▾");
		if (GUILayout.Button(bBtnText, MakeSelectableBtnStyle(_statusBasicDropdownOpen), GUILayout.Height(24f), GUILayout.ExpandWidth(true)))
		{
			_statusBasicDropdownOpen = !_statusBasicDropdownOpen;
			if (_statusBasicDropdownOpen)
			{
				_statusTargetedDropdownOpen = false;
			}
		}
		GUILayout.Space(4f);
		string tDirty = _ctrl.IsTargetedPresetDirty ? "*" : "";
		string tBtnText = $"🎯 专项预设: {_ctrl.ActivePresetName}({_ctrl.TargetedGatherKeys.Count}){tDirty} " + (_statusTargetedDropdownOpen ? "▲" : "▾");
		if (GUILayout.Button(tBtnText, MakeSelectableBtnStyle(_statusTargetedDropdownOpen || _ctrl.TargetedGatherEnabled), GUILayout.Height(24f), GUILayout.ExpandWidth(true)))
		{
			_statusTargetedDropdownOpen = !_statusTargetedDropdownOpen;
			if (_statusTargetedDropdownOpen)
			{
				_statusBasicDropdownOpen = false;
			}
		}
		GUILayout.EndHorizontal();

		if (_statusBasicDropdownOpen)
		{
			List<string> bNames = _ctrl.GetBasicPresetNames();
			GUILayout.BeginVertical(_boxStyle);
			float h = Mathf.Clamp((float)bNames.Count * 26f + 6f, 30f, 136f);
			_statusBasicDropdownScroll = GUILayout.BeginScrollView(_statusBasicDropdownScroll, GUILayout.Height(h));
			for (int i = 0; i < bNames.Count; i++)
			{
				string pName = bNames[i];
				bool isCur = string.Equals(pName, _ctrl.ActiveBasicPresetName, StringComparison.OrdinalIgnoreCase);
				GUIStyle st = MakeSelectableBtnStyle(isCur);
				st.alignment = TextAnchor.MiddleLeft;
				string summary = _ctrl.GetBasicPresetSummary(pName);
				string label = FormatNumberedPresetTitle(i, pName) + (string.IsNullOrEmpty(summary) ? "" : $"  （{summary}）") + (isCur ? " ✓" : "");
				if (GUILayout.Button(label, st, GUILayout.ExpandWidth(true), GUILayout.Height(24f)))
				{
					_ctrl.SwitchToBasicPreset(pName);
					_statusBasicDropdownOpen = false;
				}
			}
			GUILayout.EndScrollView();
			GUILayout.EndVertical();
		}

		if (_statusTargetedDropdownOpen)
		{
			List<string> tNames = _ctrl.GetPresetNames();
			GUILayout.BeginVertical(_boxStyle);
			float h = Mathf.Clamp((float)tNames.Count * 26f + 6f, 30f, 136f);
			_statusTargetedDropdownScroll = GUILayout.BeginScrollView(_statusTargetedDropdownScroll, GUILayout.Height(h));
			for (int i = 0; i < tNames.Count; i++)
			{
				string pName = tNames[i];
				bool isCur = string.Equals(pName, _ctrl.ActivePresetName, StringComparison.OrdinalIgnoreCase);
				GUIStyle st = MakeSelectableBtnStyle(isCur);
				st.alignment = TextAnchor.MiddleLeft;
				int cnt = _ctrl.GetPresetSavedItemCount(pName);
				string label = $"{FormatNumberedPresetTitle(i, pName)}  （已存 {cnt} 项）" + (isCur ? " ✓" : "");
				if (GUILayout.Button(label, st, GUILayout.ExpandWidth(true), GUILayout.Height(24f)))
				{
					_ctrl.SwitchToPreset(pName);
					_statusTargetedDropdownOpen = false;
				}
			}
			GUILayout.EndScrollView();
			GUILayout.EndVertical();
		}
	}

	private void DrawHpBar(Rect rect, float pct)
	{
		GUI.DrawTexture(rect, (Texture)(object)_lineTex);
		Rect fillRect = new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(pct), rect.height);
		GUI.DrawTexture(fillRect, (Texture)(object)_headerTex);
	}

	private void DrawSeparator()
	{
		Rect r = GUILayoutUtility.GetRect(10f, 1f, GUILayout.ExpandWidth(true));
		GUI.DrawTexture(r, (Texture)(object)_lineTex);
	}

	private void DrawTabButton(SettingsTab tab, string label)
	{
		bool isActive = _activeTab == tab;
		GUIStyle style = new GUIStyle(_tabStyle);
		if (isActive)
		{
			style.normal.background = _selectedBtnTex;
			style.hover.background = _selectedBtnTex;
			style.normal.textColor = _selectedText;
			style.hover.textColor = _selectedText;
		}
		if (GUILayout.Button(label, style, GUILayout.Height(28f)) && !isActive)
		{
			_activeTab = tab;
			_targetedPresetDropdownOpen = false;
			_creatingPreset = false;
			_editingPresetOldName = null;
			_basicPresetDropdownOpen = false;
			_creatingBasicPreset = false;
			_editingBasicPresetOldName = null;
		}
	}

	private static string FormatNumberedPresetTitle(int zeroBasedIndex, string presetName)
	{
		int num = zeroBasedIndex + 1;
		if (string.IsNullOrEmpty(presetName))
		{
			return num.ToString();
		}
		string prefixDot = num + ".";
		string prefixComma = num + "、";
		if (presetName.StartsWith(prefixDot, StringComparison.Ordinal) || presetName.StartsWith(prefixComma, StringComparison.Ordinal) || string.Equals(presetName, num.ToString(), StringComparison.Ordinal))
		{
			return presetName;
		}
		return $"{num}. {presetName}";
	}

	private static string GetNextDefaultPresetName(List<string> existingNames)
	{
		int idx = ((existingNames != null) ? existingNames.Count : 0) + 1;
		while (existingNames != null && existingNames.Any(n => string.Equals(n, $"预设{idx}", StringComparison.OrdinalIgnoreCase) || string.Equals(n, idx.ToString(), StringComparison.OrdinalIgnoreCase)))
		{
			idx++;
		}
		return $"预设{idx}";
	}

	/// <summary>⚔️ 战斗与生存页：集中管理血量/体力/负重/钓鱼保护/空闲停止、索敌策略与家禽动物攻击（零重复项）。</summary>
	private void DrawCombatTab(float contentWidth)
	{
		DrawThresholdTab(contentWidth);

		GUILayout.Space(8f);
		DrawSeparator();
		GUILayout.Space(6f);

		bool attackAnimals = GUILayout.Toggle(_ctrl.AttackChicken, " 🐔 攻击野外家禽与动物（自动保护己方与城镇）", _toggleStyle);
		GUILayout.Label("采用 Elin 原生动物判定，覆盖鸡/鸭/鹅/羊/猪/牛/兔/鹿/渡渡鸟/火鸡等中立野生动物，不误伤己方宠物与城镇动物。", _labelSubStyle);
		if (attackAnimals != _ctrl.AttackChicken)
		{
			_ctrl.AttackChicken = attackAnimals;
			_ctrl.SaveConfig();
		}

		GUILayout.Space(8f);
		DrawSeparator();
		GUILayout.Space(6f);

		bool nextIdleStop = GUILayout.Toggle(_ctrl.IdleAutoStopEnabled, " ⏱️ 空闲/受阻 N 秒后自动停止脚本", _toggleStyle);
		if (nextIdleStop != _ctrl.IdleAutoStopEnabled)
		{
			_ctrl.IdleAutoStopEnabled = nextIdleStop;
			_ctrl.SaveConfig();
		}
		if (_ctrl.IdleAutoStopEnabled)
		{
			GUILayout.Label($"空闲/受阻超时: {_ctrl.IdleAutoStopSeconds} 秒", _labelStyle);
			int idleSec = (int)GUILayout.HorizontalSlider((float)_ctrl.IdleAutoStopSeconds, 3f, 60f);
			if (idleSec != _ctrl.IdleAutoStopSeconds)
			{
				_ctrl.IdleAutoStopSeconds = idleSec;
				_ctrl.SaveConfig();
			}
		}
		GUILayout.Label("资源采尽或被深水/隔墙完全挡住达指定秒数时自动停止；进入世界大地图立即停止。", _labelSubStyle);
	}

	/// <summary>⚙️ 配置页：低频设置集中收纳（基础预设管理、快捷栏槽位分配、行动边界与调试日志）。</summary>
	private void DrawConfigTab(float contentWidth)
	{
		DrawBasicPresetBar(contentWidth);

		GUILayout.Space(8f);
		DrawSeparator();
		GUILayout.Space(6f);

		GUILayout.Label("【一、快捷栏槽位分配】 · 自动读取当前槽位物品名，点「无」表示不用", _fieldLabelStyle);
		GUILayout.Space(4f);
		DrawSlotsTab(contentWidth);

		GUILayout.Space(8f);
		DrawSeparator();
		GUILayout.Space(6f);

		GUILayout.Label("【二、可行动范围边界与调试日志】", _fieldLabelStyle);
		GUILayout.Space(4f);
		DrawMoreTab(contentWidth);
	}

	/// <summary>基础设置页（合并原「快捷栏」「阈值」「更多」三部分，并配备包含全部基础设置项的预设系统）。</summary>
	private void DrawBasicSettingsTab(float contentWidth)
	{
		DrawBasicPresetBar(contentWidth);

		GUILayout.Space(6f);
		DrawSeparator();
		GUILayout.Space(6f);

		// 子分区筛选条（方便一页全览或单独聚焦某一部分）
		GUILayout.BeginHorizontal();
		float quarter = (contentWidth - 20f) / 4f;
		DrawBasicSubButton(BasicSubSection.All, "📋 全部显示", quarter);
		DrawBasicSubButton(BasicSubSection.Slots, "🔢 快捷栏", quarter);
		DrawBasicSubButton(BasicSubSection.Threshold, "⚖️ 阈值与索敌", quarter);
		DrawBasicSubButton(BasicSubSection.More, "⚙️ 更多与范围", quarter);
		GUILayout.EndHorizontal();

		GUILayout.Space(6f);
		DrawSeparator();
		GUILayout.Space(6f);

		if (_basicSubSection == BasicSubSection.All || _basicSubSection == BasicSubSection.Slots)
		{
			GUILayout.Label("【一、快捷栏槽位分配】 · 点数字选择，\"无\"表示不用", _fieldLabelStyle);
			GUILayout.Space(2f);
			DrawSlotsTab(contentWidth);
		}

		if (_basicSubSection == BasicSubSection.All)
		{
			GUILayout.Space(4f);
			DrawSeparator();
			GUILayout.Space(6f);
		}

		if (_basicSubSection == BasicSubSection.All || _basicSubSection == BasicSubSection.Threshold)
		{
			GUILayout.Label("【二、生存阈值与战斗/拾取策略】", _fieldLabelStyle);
			GUILayout.Space(2f);
			DrawThresholdTab(contentWidth);
		}

		if (_basicSubSection == BasicSubSection.All)
		{
			GUILayout.Space(6f);
			DrawSeparator();
			GUILayout.Space(6f);
		}

		if (_basicSubSection == BasicSubSection.All || _basicSubSection == BasicSubSection.More)
		{
			GUILayout.Label("【三、更多设置与可行动边界】", _fieldLabelStyle);
			GUILayout.Space(2f);
			DrawMoreTab(contentWidth);
		}
	}

	private void DrawBasicSubButton(BasicSubSection sub, string label, float width)
	{
		bool active = _basicSubSection == sub;
		if (GUILayout.Button(label, MakeSelectableBtnStyle(active), GUILayout.Width(width), GUILayout.Height(24f)) && !active)
		{
			_basicSubSection = sub;
		}
	}

	/// <summary>基础设置预设下拉栏（覆盖快捷栏、阈值、战斗目标选择、更多设置全部基础项）。</summary>
	private void DrawBasicPresetBar(float contentWidth)
	{
		List<string> presetNames = _ctrl.GetBasicPresetNames();
		int activeIdx = presetNames.FindIndex(n => string.Equals(n, _ctrl.ActiveBasicPresetName, StringComparison.OrdinalIgnoreCase));
		if (activeIdx < 0)
		{
			activeIdx = 0;
		}
		bool isDirty = _ctrl.IsBasicPresetDirty;
		bool showSaveFeedback = Time.realtimeSinceStartup < _basicSaveFeedbackUntil;

		GUILayout.BeginHorizontal();
		GUILayout.Label("基础设置预设（含快捷栏/阈值/战斗策略/更多全部参数 · 点【保存】写入预设）：", _fieldLabelStyle);
		GUILayout.FlexibleSpace();
		if (showSaveFeedback)
		{
			GUIStyle savedBadgeStyle = new GUIStyle(_fieldLabelStyle);
			savedBadgeStyle.normal.textColor = _selectedOrange;
			GUILayout.Label("✓ 已写入基础预设", savedBadgeStyle);
		}
		else if (isDirty)
		{
			GUIStyle dirtyBadgeStyle = new GUIStyle(_fieldLabelStyle);
			dirtyBadgeStyle.normal.textColor = _selectedOrange;
			GUILayout.Label("● 有未存预设改动", dirtyBadgeStyle);
		}
		GUILayout.EndHorizontal();

		GUILayout.Space(2f);
		GUILayout.BeginHorizontal();

		Rect comboRect = GUILayoutUtility.GetRect(120f, 28f, GUILayout.ExpandWidth(true));
		Event ev = Event.current;
		bool isHovered = ev != null && comboRect.Contains(ev.mousePosition);
		float arrowBoxWidth = 28f;

		GUI.DrawTexture(comboRect, (Texture)(object)_headerTex);
		Rect innerRect = new Rect(comboRect.x + 1f, comboRect.y + 1f, comboRect.width - 2f, comboRect.height - 2f);
		Texture2D mainBg = _basicPresetDropdownOpen ? _selectedBtnTex : (isHovered ? _btnHoverTex : _bgTex);
		GUI.DrawTexture(innerRect, (Texture)(object)mainBg);

		Rect dividerRect = new Rect(comboRect.xMax - arrowBoxWidth, comboRect.y + 1f, 1f, comboRect.height - 2f);
		Rect arrowRect = new Rect(comboRect.xMax - arrowBoxWidth + 1f, comboRect.y + 1f, arrowBoxWidth - 2f, comboRect.height - 2f);
		Texture2D arrowBg = _basicPresetDropdownOpen ? _selectedBtnTex : (isHovered ? _btnHoverTex : _btnTex);
		GUI.DrawTexture(arrowRect, (Texture)(object)arrowBg);
		GUI.DrawTexture(dividerRect, (Texture)(object)_headerTex);

		string dirtyMark = isDirty ? "  *未保存" : "";
		string summary = _ctrl.GetBasicPresetSummary(_ctrl.ActiveBasicPresetName);
		string summaryPart = string.IsNullOrEmpty(summary) ? "" : $"  （{summary}）";
		string displayTitle = $"{FormatNumberedPresetTitle(activeIdx, _ctrl.ActiveBasicPresetName)}{summaryPart}{dirtyMark}";
		GUIStyle comboTextStyle = new GUIStyle(_labelStyle);
		comboTextStyle.fontStyle = FontStyle.Bold;
		comboTextStyle.alignment = TextAnchor.MiddleLeft;
		comboTextStyle.normal.textColor = (_basicPresetDropdownOpen || isHovered || isDirty) ? _selectedOrange : _textColor;
		GUI.Label(new Rect(comboRect.x + 10f, comboRect.y, comboRect.width - arrowBoxWidth - 14f, comboRect.height), displayTitle, comboTextStyle);

		GUIStyle arrowTextStyle = new GUIStyle(_labelStyle);
		arrowTextStyle.fontStyle = FontStyle.Bold;
		arrowTextStyle.fontSize = 12;
		arrowTextStyle.alignment = TextAnchor.MiddleCenter;
		arrowTextStyle.normal.textColor = (_basicPresetDropdownOpen || isHovered) ? _selectedOrange : _textColor;
		GUI.Label(arrowRect, _basicPresetDropdownOpen ? "▲" : "▼", arrowTextStyle);

		if (GUI.Button(comboRect, GUIContent.none, GUIStyle.none))
		{
			_basicPresetDropdownOpen = !_basicPresetDropdownOpen;
			if (_basicPresetDropdownOpen)
			{
				_creatingBasicPreset = false;
				_editingBasicPresetOldName = null;
			}
		}

		GUILayout.Space(4f);

		string saveBtnLabel = showSaveFeedback ? "✓ 已存" : (isDirty ? "💾 保存*" : "💾 保存");
		GUIStyle saveBtnStyle = MakeSelectableBtnStyle(isDirty || showSaveFeedback);
		if (GUILayout.Button(saveBtnLabel, saveBtnStyle, GUILayout.Width(68f), GUILayout.Height(28f)))
		{
			_ctrl.SaveCurrentBasicPreset();
			_basicSaveFeedbackUntil = Time.realtimeSinceStartup + 1.5f;
			_basicPresetDropdownOpen = false;
		}

		if (GUILayout.Button("+ 新增", MakeSelectableBtnStyle(_creatingBasicPreset), GUILayout.Width(56f), GUILayout.Height(28f)))
		{
			_creatingBasicPreset = !_creatingBasicPreset;
			_editingBasicPresetOldName = null;
			_basicPresetDropdownOpen = false;
			if (_creatingBasicPreset)
			{
				_basicPresetInlineBuffer = GetNextDefaultPresetName(presetNames);
				_pendingFocusBasicPresetInput = true;
				Input.imeCompositionMode = IMECompositionMode.On;
			}
		}

		bool isRenaming = _editingBasicPresetOldName != null;
		if (GUILayout.Button("✎", MakeSelectableBtnStyle(isRenaming), GUILayout.Width(26f), GUILayout.Height(28f)))
		{
			if (isRenaming)
			{
				_editingBasicPresetOldName = null;
				_pendingFocusBasicPresetInput = false;
			}
			else
			{
				_editingBasicPresetOldName = _ctrl.ActiveBasicPresetName;
				_creatingBasicPreset = false;
				_basicPresetDropdownOpen = false;
				_basicPresetInlineBuffer = _ctrl.ActiveBasicPresetName;
				_pendingFocusBasicPresetInput = true;
				Input.imeCompositionMode = IMECompositionMode.On;
			}
		}
		if (presetNames.Count > 1 && GUILayout.Button("×", _btnStyle, GUILayout.Width(24f), GUILayout.Height(28f)))
		{
			_ctrl.DeleteBasicPreset(_ctrl.ActiveBasicPresetName);
			_basicPresetDropdownOpen = false;
			_creatingBasicPreset = false;
			_editingBasicPresetOldName = null;
		}

		GUILayout.EndHorizontal();

		if (_basicPresetDropdownOpen)
		{
			GUILayout.Space(2f);
			GUILayout.BeginVertical(_boxStyle);
			float itemRowHeight = 26f;
			float listHeight = Mathf.Clamp((float)presetNames.Count * (itemRowHeight + 2f) + 6f, 32f, 176f);
			_basicPresetScrollPosition = GUILayout.BeginScrollView(_basicPresetScrollPosition, GUILayout.Height(listHeight));
			for (int i = 0; i < presetNames.Count; i++)
			{
				string pName = presetNames[i];
				bool isCurrent = string.Equals(pName, _ctrl.ActiveBasicPresetName, StringComparison.OrdinalIgnoreCase);
				string pSummary = _ctrl.GetBasicPresetSummary(pName);

				GUIStyle itemStyle = new GUIStyle(_btnStyle);
				itemStyle.alignment = TextAnchor.MiddleLeft;
				itemStyle.padding = new RectOffset(10, 10, 3, 3);
				if (isCurrent)
				{
					itemStyle.normal.background = _selectedBtnTex;
					itemStyle.hover.background = _selectedBtnTex;
					itemStyle.normal.textColor = _selectedOrange;
					itemStyle.hover.textColor = _selectedOrange;
				}

				string suffix = isCurrent ? (isDirty ? $"  （{pSummary} · 点此回退未存改动）" : $"  （{pSummary}） ✓") : $"  （{pSummary}）";
				string rowText = FormatNumberedPresetTitle(i, pName) + suffix;
				if (GUILayout.Button(rowText, itemStyle, GUILayout.ExpandWidth(true), GUILayout.Height(itemRowHeight)))
				{
					_ctrl.SwitchToBasicPreset(pName);
					_basicPresetDropdownOpen = false;
				}
			}
			GUILayout.EndScrollView();
			GUILayout.EndVertical();
		}

		if (_creatingBasicPreset)
		{
			Input.imeCompositionMode = IMECompositionMode.On;
			GUILayout.Space(4f);
			GUILayout.BeginHorizontal();
			GUILayout.Label("新基础预设:", _labelStyle, GUILayout.Width(72f));
			GUI.SetNextControlName("basicPresetInlineEdit");
			_basicPresetInlineBuffer = GUILayout.TextField(_basicPresetInlineBuffer ?? "", GUILayout.ExpandWidth(true));
			if (_pendingFocusBasicPresetInput)
			{
				GUI.FocusControl("basicPresetInlineEdit");
				if (ev != null && ev.type == EventType.Repaint && GUI.GetNameOfFocusedControl() == "basicPresetInlineEdit")
				{
					_pendingFocusBasicPresetInput = false;
				}
			}
			if (GUILayout.Button("粘贴", _btnStyle, GUILayout.Width(38f), GUILayout.Height(22f)))
			{
				string clip = GUIUtility.systemCopyBuffer;
				if (!string.IsNullOrEmpty(clip))
				{
					_basicPresetInlineBuffer = (_basicPresetInlineBuffer ?? "") + clip.Replace("\r", "").Replace("\n", "").Trim();
				}
				_pendingFocusBasicPresetInput = true;
			}
			bool enterCreate = ev != null && ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "basicPresetInlineEdit" && string.IsNullOrEmpty(Input.compositionString);
			if (GUILayout.Button("✓ 存当前", _btnStyle, GUILayout.Width(68f), GUILayout.Height(22f)) || enterCreate)
			{
				string targetName = string.IsNullOrWhiteSpace(_basicPresetInlineBuffer) ? GetNextDefaultPresetName(presetNames) : _basicPresetInlineBuffer.Trim();
				_ctrl.SaveAsNewBasicPreset(targetName, resetToDefault: false);
				_basicSaveFeedbackUntil = Time.realtimeSinceStartup + 1.5f;
				_creatingBasicPreset = false;
				_pendingFocusBasicPresetInput = false;
				if (enterCreate && ev != null) ev.Use();
			}
			if (GUILayout.Button("✓ 初始值", _btnStyle, GUILayout.Width(64f), GUILayout.Height(22f)))
			{
				string targetName = string.IsNullOrWhiteSpace(_basicPresetInlineBuffer) ? GetNextDefaultPresetName(presetNames) : _basicPresetInlineBuffer.Trim();
				_ctrl.SaveAsNewBasicPreset(targetName, resetToDefault: true);
				_basicSaveFeedbackUntil = Time.realtimeSinceStartup + 1.5f;
				_creatingBasicPreset = false;
				_pendingFocusBasicPresetInput = false;
			}
			if (GUILayout.Button("取消", _btnStyle, GUILayout.Width(40f), GUILayout.Height(22f)))
			{
				_creatingBasicPreset = false;
				_pendingFocusBasicPresetInput = false;
			}
			GUILayout.EndHorizontal();
		}

		if (_editingBasicPresetOldName != null)
		{
			Input.imeCompositionMode = IMECompositionMode.On;
			GUILayout.Space(4f);
			GUILayout.BeginHorizontal();
			GUILayout.Label("重命名预设:", _labelStyle, GUILayout.Width(72f));
			GUI.SetNextControlName("basicPresetInlineEdit");
			_basicPresetInlineBuffer = GUILayout.TextField(_basicPresetInlineBuffer ?? "", GUILayout.ExpandWidth(true));
			if (_pendingFocusBasicPresetInput)
			{
				GUI.FocusControl("basicPresetInlineEdit");
				if (ev != null && ev.type == EventType.Repaint && GUI.GetNameOfFocusedControl() == "basicPresetInlineEdit")
				{
					_pendingFocusBasicPresetInput = false;
				}
			}
			if (GUILayout.Button("粘贴", _btnStyle, GUILayout.Width(38f), GUILayout.Height(22f)))
			{
				string clip = GUIUtility.systemCopyBuffer;
				if (!string.IsNullOrEmpty(clip))
				{
					_basicPresetInlineBuffer = (_basicPresetInlineBuffer ?? "") + clip.Replace("\r", "").Replace("\n", "").Trim();
				}
				_pendingFocusBasicPresetInput = true;
			}
			bool enterRename = ev != null && ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "basicPresetInlineEdit" && string.IsNullOrEmpty(Input.compositionString);
			if (GUILayout.Button("✓ 确认改名", _btnStyle, GUILayout.Width(76f), GUILayout.Height(22f)) || enterRename)
			{
				if (!string.IsNullOrWhiteSpace(_basicPresetInlineBuffer))
				{
					_ctrl.RenameBasicPreset(_editingBasicPresetOldName, _basicPresetInlineBuffer.Trim());
				}
				_editingBasicPresetOldName = null;
				_pendingFocusBasicPresetInput = false;
				if (enterRename && ev != null) ev.Use();
			}
			if (GUILayout.Button("取消", _btnStyle, GUILayout.Width(40f), GUILayout.Height(22f)))
			{
				_editingBasicPresetOldName = null;
				_pendingFocusBasicPresetInput = false;
			}
			GUILayout.EndHorizontal();
		}

		if (isDirty)
		{
			GUILayout.Space(3f);
			GUILayout.BeginHorizontal();
			GUILayout.Label("⚠ 基础设置改动尚未写入当前基础预设：点【💾 保存*】可保存，或点右侧回退恢复预设值。", _labelSubStyle);
			GUILayout.FlexibleSpace();
			if (GUILayout.Button("↩ 回退", _btnStyle, GUILayout.Width(56f), GUILayout.Height(20f)))
			{
				_ctrl.RevertCurrentBasicPreset();
			}
			GUILayout.EndHorizontal();
		}
	}

	/// <summary>专项页酒馆式拉取下拉预设栏：长方形选择框 + 右侧箭头（点击展开 1,2,3,4,5,6... 预设列表，点选立即切换）+ 显式【保存】按钮 + 【新增】按钮。</summary>
	private void DrawTargetedPresetBar(float contentWidth)
	{
		List<string> presetNames = _ctrl.GetPresetNames();
		int activeIdx = presetNames.FindIndex(n => string.Equals(n, _ctrl.ActivePresetName, StringComparison.OrdinalIgnoreCase));
		if (activeIdx < 0)
		{
			activeIdx = 0;
		}
		bool isDirty = _ctrl.IsTargetedPresetDirty;
		bool showSaveFeedback = Time.realtimeSinceStartup < _saveFeedbackUntil;

		GUILayout.BeginHorizontal();
		GUILayout.Label("专项预设（拉取切换 · 点【保存】才写入，不保存直接切换即可回退）：", _fieldLabelStyle);
		GUILayout.FlexibleSpace();
		if (showSaveFeedback)
		{
			GUIStyle savedBadgeStyle = new GUIStyle(_fieldLabelStyle);
			savedBadgeStyle.normal.textColor = _selectedOrange;
			GUILayout.Label("✓ 已写入当前预设", savedBadgeStyle);
		}
		else if (isDirty)
		{
			GUIStyle dirtyBadgeStyle = new GUIStyle(_fieldLabelStyle);
			dirtyBadgeStyle.normal.textColor = _selectedOrange;
			GUILayout.Label("● 有未保存修改", dirtyBadgeStyle);
		}
		GUILayout.EndHorizontal();

		GUILayout.Space(2f);
		GUILayout.BeginHorizontal();

		// ---------- 1. 长方形下拉框 + 右侧箭头 ----------
		Rect comboRect = GUILayoutUtility.GetRect(120f, 28f, GUILayout.ExpandWidth(true));
		Event ev = Event.current;
		bool isHovered = ev != null && comboRect.Contains(ev.mousePosition);
		float arrowBoxWidth = 28f;

		// 外框 1px 描边
		GUI.DrawTexture(comboRect, (Texture)(object)_headerTex);
		// 内部长方形底色
		Rect innerRect = new Rect(comboRect.x + 1f, comboRect.y + 1f, comboRect.width - 2f, comboRect.height - 2f);
		Texture2D mainBg = _targetedPresetDropdownOpen ? _selectedBtnTex : (isHovered ? _btnHoverTex : _bgTex);
		GUI.DrawTexture(innerRect, (Texture)(object)mainBg);

		// 右侧箭头独立分格底色与分割线
		Rect dividerRect = new Rect(comboRect.xMax - arrowBoxWidth, comboRect.y + 1f, 1f, comboRect.height - 2f);
		Rect arrowRect = new Rect(comboRect.xMax - arrowBoxWidth + 1f, comboRect.y + 1f, arrowBoxWidth - 2f, comboRect.height - 2f);
		Texture2D arrowBg = _targetedPresetDropdownOpen ? _selectedBtnTex : (isHovered ? _btnHoverTex : _btnTex);
		GUI.DrawTexture(arrowRect, (Texture)(object)arrowBg);
		GUI.DrawTexture(dividerRect, (Texture)(object)_headerTex);

		// 长方形左侧预设名文本
		string dirtyMark = isDirty ? "  *未保存" : "";
		string displayTitle = $"{FormatNumberedPresetTitle(activeIdx, _ctrl.ActivePresetName)}  （已选 {_ctrl.TargetedGatherKeys.Count} 项）{dirtyMark}";
		GUIStyle comboTextStyle = new GUIStyle(_labelStyle);
		comboTextStyle.fontStyle = FontStyle.Bold;
		comboTextStyle.alignment = TextAnchor.MiddleLeft;
		comboTextStyle.normal.textColor = (_targetedPresetDropdownOpen || isHovered || isDirty) ? _selectedOrange : _textColor;
		GUI.Label(new Rect(comboRect.x + 10f, comboRect.y, comboRect.width - arrowBoxWidth - 14f, comboRect.height), displayTitle, comboTextStyle);

		// 右侧 ▼ / ▲ 箭头
		GUIStyle arrowTextStyle = new GUIStyle(_labelStyle);
		arrowTextStyle.fontStyle = FontStyle.Bold;
		arrowTextStyle.fontSize = 12;
		arrowTextStyle.alignment = TextAnchor.MiddleCenter;
		arrowTextStyle.normal.textColor = (_targetedPresetDropdownOpen || isHovered) ? _selectedOrange : _textColor;
		GUI.Label(arrowRect, _targetedPresetDropdownOpen ? "▲" : "▼", arrowTextStyle);

		if (GUI.Button(comboRect, GUIContent.none, GUIStyle.none))
		{
			_targetedPresetDropdownOpen = !_targetedPresetDropdownOpen;
			if (_targetedPresetDropdownOpen)
			{
				_creatingPreset = false;
				_editingPresetOldName = null;
			}
		}

		GUILayout.Space(4f);

		// ---------- 2. 显式【保存】按钮（只有点击此按钮才把当前勾选写入当前预设） ----------
		string saveBtnLabel = showSaveFeedback ? "✓ 已存" : (isDirty ? "💾 保存*" : "💾 保存");
		GUIStyle saveBtnStyle = MakeSelectableBtnStyle(isDirty || showSaveFeedback);
		if (GUILayout.Button(saveBtnLabel, saveBtnStyle, GUILayout.Width(68f), GUILayout.Height(28f)))
		{
			_ctrl.SaveCurrentTargetedPreset();
			_saveFeedbackUntil = Time.realtimeSinceStartup + 1.5f;
			_targetedPresetDropdownOpen = false;
		}

		// ---------- 3. 【新增】按钮 ----------
		if (GUILayout.Button("+ 新增", MakeSelectableBtnStyle(_creatingPreset), GUILayout.Width(56f), GUILayout.Height(28f)))
		{
			_creatingPreset = !_creatingPreset;
			_editingPresetOldName = null;
			_targetedPresetDropdownOpen = false;
			if (_creatingPreset)
			{
				_presetInlineBuffer = GetNextDefaultPresetName(presetNames);
				_pendingFocusPresetInput = true;
				Input.imeCompositionMode = IMECompositionMode.On;
			}
		}

		// ---------- 4. 改名 (✎) 与删除 (×) 按钮 ----------
		bool isRenaming = _editingPresetOldName != null;
		if (GUILayout.Button("✎", MakeSelectableBtnStyle(isRenaming), GUILayout.Width(26f), GUILayout.Height(28f)))
		{
			if (isRenaming)
			{
				_editingPresetOldName = null;
				_pendingFocusPresetInput = false;
			}
			else
			{
				_editingPresetOldName = _ctrl.ActivePresetName;
				_creatingPreset = false;
				_targetedPresetDropdownOpen = false;
				_presetInlineBuffer = _ctrl.ActivePresetName;
				_pendingFocusPresetInput = true;
				Input.imeCompositionMode = IMECompositionMode.On;
			}
		}
		if (presetNames.Count > 1 && GUILayout.Button("×", _btnStyle, GUILayout.Width(24f), GUILayout.Height(28f)))
		{
			_ctrl.DeletePreset(_ctrl.ActivePresetName);
			_targetedPresetDropdownOpen = false;
			_creatingPreset = false;
			_editingPresetOldName = null;
		}

		GUILayout.EndHorizontal();

		// ---------- 展开的拉取式下拉列表（1, 2, 3, 4, 5, 6... 点选即切换） ----------
		if (_targetedPresetDropdownOpen)
		{
			GUILayout.Space(2f);
			GUILayout.BeginVertical(_boxStyle);
			float itemRowHeight = 26f;
			float listHeight = Mathf.Clamp((float)presetNames.Count * (itemRowHeight + 2f) + 6f, 32f, 176f);
			_presetScrollPosition = GUILayout.BeginScrollView(_presetScrollPosition, GUILayout.Height(listHeight));
			for (int i = 0; i < presetNames.Count; i++)
			{
				string pName = presetNames[i];
				bool isCurrent = string.Equals(pName, _ctrl.ActivePresetName, StringComparison.OrdinalIgnoreCase);
				int savedCount = _ctrl.GetPresetSavedItemCount(pName);

				GUIStyle itemStyle = new GUIStyle(_btnStyle);
				itemStyle.alignment = TextAnchor.MiddleLeft;
				itemStyle.padding = new RectOffset(10, 10, 3, 3);
				if (isCurrent)
				{
					itemStyle.normal.background = _selectedBtnTex;
					itemStyle.hover.background = _selectedBtnTex;
					itemStyle.normal.textColor = _selectedOrange;
					itemStyle.hover.textColor = _selectedOrange;
				}

				string suffix = isCurrent ? (isDirty ? $"  （已存 {savedCount} 项 · 点此回退未存改动）" : $"  （已存 {savedCount} 项） ✓") : $"  （已存 {savedCount} 项）";
				string rowText = FormatNumberedPresetTitle(i, pName) + suffix;
				if (GUILayout.Button(rowText, itemStyle, GUILayout.ExpandWidth(true), GUILayout.Height(itemRowHeight)))
				{
					_ctrl.SwitchToPreset(pName);
					_targetedPresetDropdownOpen = false;
				}
			}
			GUILayout.EndScrollView();
			GUILayout.EndVertical();
		}

		// ---------- 新增预设内联输入栏 ----------
		if (_creatingPreset)
		{
			Input.imeCompositionMode = IMECompositionMode.On;
			GUILayout.Space(4f);
			GUILayout.BeginHorizontal();
			GUILayout.Label("新增预设名:", _labelStyle, GUILayout.Width(70f));
			GUI.SetNextControlName("presetInlineEdit");
			_presetInlineBuffer = GUILayout.TextField(_presetInlineBuffer ?? "", GUILayout.ExpandWidth(true));
			if (_pendingFocusPresetInput)
			{
				GUI.FocusControl("presetInlineEdit");
				if (ev != null && ev.type == EventType.Repaint && GUI.GetNameOfFocusedControl() == "presetInlineEdit")
				{
					_pendingFocusPresetInput = false;
				}
			}
			if (GUILayout.Button("粘贴", _btnStyle, GUILayout.Width(38f), GUILayout.Height(22f)))
			{
				string clip = GUIUtility.systemCopyBuffer;
				if (!string.IsNullOrEmpty(clip))
				{
					_presetInlineBuffer = (_presetInlineBuffer ?? "") + clip.Replace("\r", "").Replace("\n", "").Trim();
				}
				_pendingFocusPresetInput = true;
			}
			bool enterCreate = ev != null && ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "presetInlineEdit" && string.IsNullOrEmpty(Input.compositionString);
			if (GUILayout.Button("✓ 存已选", _btnStyle, GUILayout.Width(68f), GUILayout.Height(22f)) || enterCreate)
			{
				string targetName = string.IsNullOrWhiteSpace(_presetInlineBuffer) ? GetNextDefaultPresetName(presetNames) : _presetInlineBuffer.Trim();
				_ctrl.SaveAsNewPreset(targetName, startEmpty: false);
				_saveFeedbackUntil = Time.realtimeSinceStartup + 1.5f;
				_creatingPreset = false;
				_pendingFocusPresetInput = false;
				if (enterCreate && ev != null) ev.Use();
			}
			if (GUILayout.Button("✓ 空白", _btnStyle, GUILayout.Width(56f), GUILayout.Height(22f)))
			{
				string targetName = string.IsNullOrWhiteSpace(_presetInlineBuffer) ? GetNextDefaultPresetName(presetNames) : _presetInlineBuffer.Trim();
				_ctrl.SaveAsNewPreset(targetName, startEmpty: true);
				_saveFeedbackUntil = Time.realtimeSinceStartup + 1.5f;
				_creatingPreset = false;
				_pendingFocusPresetInput = false;
			}
			if (GUILayout.Button("取消", _btnStyle, GUILayout.Width(40f), GUILayout.Height(22f)))
			{
				_creatingPreset = false;
				_pendingFocusPresetInput = false;
			}
			GUILayout.EndHorizontal();
		}

		// ---------- 重命名预设内联输入栏 ----------
		if (_editingPresetOldName != null)
		{
			Input.imeCompositionMode = IMECompositionMode.On;
			GUILayout.Space(4f);
			GUILayout.BeginHorizontal();
			GUILayout.Label("重命名预设:", _labelStyle, GUILayout.Width(70f));
			GUI.SetNextControlName("presetInlineEdit");
			_presetInlineBuffer = GUILayout.TextField(_presetInlineBuffer ?? "", GUILayout.ExpandWidth(true));
			if (_pendingFocusPresetInput)
			{
				GUI.FocusControl("presetInlineEdit");
				if (ev != null && ev.type == EventType.Repaint && GUI.GetNameOfFocusedControl() == "presetInlineEdit")
				{
					_pendingFocusPresetInput = false;
				}
			}
			if (GUILayout.Button("粘贴", _btnStyle, GUILayout.Width(38f), GUILayout.Height(22f)))
			{
				string clip = GUIUtility.systemCopyBuffer;
				if (!string.IsNullOrEmpty(clip))
				{
					_presetInlineBuffer = (_presetInlineBuffer ?? "") + clip.Replace("\r", "").Replace("\n", "").Trim();
				}
				_pendingFocusPresetInput = true;
			}
			bool enterRename = ev != null && ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "presetInlineEdit" && string.IsNullOrEmpty(Input.compositionString);
			if (GUILayout.Button("✓ 确认改名", _btnStyle, GUILayout.Width(76f), GUILayout.Height(22f)) || enterRename)
			{
				if (!string.IsNullOrWhiteSpace(_presetInlineBuffer))
				{
					_ctrl.RenamePreset(_editingPresetOldName, _presetInlineBuffer.Trim());
				}
				_editingPresetOldName = null;
				_pendingFocusPresetInput = false;
				if (enterRename && ev != null) ev.Use();
			}
			if (GUILayout.Button("取消", _btnStyle, GUILayout.Width(40f), GUILayout.Height(22f)))
			{
				_editingPresetOldName = null;
				_pendingFocusPresetInput = false;
			}
			GUILayout.EndHorizontal();
		}

		// ---------- 未保存改动的快捷回退提示条 ----------
		if (isDirty)
		{
			GUILayout.Space(3f);
			GUILayout.BeginHorizontal();
			GUILayout.Label("⚠ 改动尚未写入预设：点【💾 保存*】可保存；若选错了不点保存直接下拉切换预设（或点右侧回退）即可恢复。", _labelSubStyle);
			GUILayout.FlexibleSpace();
			if (GUILayout.Button("↩ 回退", _btnStyle, GUILayout.Width(56f), GUILayout.Height(20f)))
			{
				_ctrl.RevertCurrentTargetedPreset();
			}
			GUILayout.EndHorizontal();
		}
	}

	private void DrawSlotsTab(float contentWidth)
	{
		DrawSlotGrid("战斗武器", _ctrl.CombatSlot, v => _ctrl.CombatSlot = v, contentWidth);
		DrawSlotGrid("挖矿工具", _ctrl.MiningSlot, v => _ctrl.MiningSlot = v, contentWidth);
		DrawSlotGrid("砍树工具", _ctrl.ChoppingSlot, v => _ctrl.ChoppingSlot = v, contentWidth);
		DrawSlotGrid("分解工具", _ctrl.DisassembleSlot, v => _ctrl.DisassembleSlot = v, contentWidth);
		DrawSlotGrid("镰刀(种子)", _ctrl.SickleSlot, v => _ctrl.SickleSlot = v, contentWidth);
	}

	private void DrawSlotGrid(string label, int currentValue, Action<int> setValue, float contentWidth)
	{
		string itemHint = "（未分配）";
		if (currentValue >= 0)
		{
			Thing slotThing = ToolSwitcher.GetSlotThing(currentValue);
			string itemName = (slotThing != null && !string.IsNullOrEmpty(((Card)slotThing).Name)) ? ((Card)slotThing).Name : "空槽位";
			itemHint = $" · 槽{currentValue + 1}（{itemName}）";
		}
		GUILayout.Label(label + itemHint, _labelStyle);
		GUILayout.BeginHorizontal();
		float cellSize = Mathf.Max(20f, (contentWidth - 24f) / 10f);
		for (int i = -1; i <= 8; i++)
		{
			bool selected = currentValue == i;
			GUIStyle cellStyle = new GUIStyle(_slotCellStyle);
			if (selected)
			{
				cellStyle.normal.background = _selectedBtnTex;
				cellStyle.hover.background = _selectedBtnTex;
				cellStyle.normal.textColor = _selectedText;
				cellStyle.hover.textColor = _selectedText;
				cellStyle.fontStyle = FontStyle.Bold;
			}
			string cellLabel = (i == -1) ? "无" : (i + 1).ToString();
			if (GUILayout.Button(cellLabel, cellStyle, GUILayout.Width(cellSize), GUILayout.Height(22f)) && !selected)
			{
				setValue(i);
				_ctrl.SaveConfig();
			}
		}
		GUILayout.EndHorizontal();
		GUILayout.Space(6f);
	}

	private void DrawThresholdTab(float contentWidth)
	{
		GUILayout.Label($"风筝血量: {_ctrl.KiteHpPercent:P0}", _labelStyle);
		float kite = GUILayout.HorizontalSlider(_ctrl.KiteHpPercent, 0.1f, 0.9f);
		if (Mathf.Abs(kite - _ctrl.KiteHpPercent) > 0.001f)
		{
			_ctrl.KiteHpPercent = kite;
			_ctrl.SaveConfig();
		}
		GUILayout.Space(6f);

		GUILayout.Label($"撤退血量: {_ctrl.RetreatHpPercent:P0}", _labelStyle);
		float retreat = GUILayout.HorizontalSlider(_ctrl.RetreatHpPercent, 0.05f, 0.5f);
		if (Mathf.Abs(retreat - _ctrl.RetreatHpPercent) > 0.001f)
		{
			_ctrl.RetreatHpPercent = retreat;
			_ctrl.SaveConfig();
		}
		GUILayout.Space(6f);

		GUILayout.Label($"最低体力: {_ctrl.MaxStamina}", _labelStyle);
		int stamina = (int)GUILayout.HorizontalSlider((float)_ctrl.MaxStamina, 1f, 20f);
		if (stamina != _ctrl.MaxStamina)
		{
			_ctrl.MaxStamina = stamina;
			_ctrl.SaveConfig();
		}
		GUILayout.Space(8f);
		DrawSeparator();
		GUILayout.Space(6f);

		DrawBurdenStopControls();

		GUILayout.Space(6f);
		bool nextFishProt = GUILayout.Toggle(_ctrl.FishingProtectionEnabled, " 🎣 启用钓鱼保护（仅钓鱼时掉血自动停竿，不钓鱼时不影响其他功能）", _toggleStyle);
		if (nextFishProt != _ctrl.FishingProtectionEnabled)
		{
			_ctrl.FishingProtectionEnabled = nextFishProt;
			_ctrl.SaveConfig();
		}

		GUILayout.Space(8f);
		DrawSeparator();
		GUILayout.Space(6f);

		GUILayout.Label("战斗侦测范围", _labelStyle);
		GUILayout.BeginHorizontal();
		bool isFullMap = _ctrl.combatScanMode == AutoController.CombatScanMode.FullMap;
		bool isNearby = _ctrl.combatScanMode == AutoController.CombatScanMode.Nearby;
		float halfWidth = (contentWidth - 12f) / 2f;

		if (GUILayout.Button("全图扫描", MakeSelectableBtnStyle(isFullMap), GUILayout.Width(halfWidth)) && !isFullMap)
		{
			_ctrl.combatScanMode = AutoController.CombatScanMode.FullMap;
			_ctrl.SaveConfig();
		}
		if (GUILayout.Button("周边范围", MakeSelectableBtnStyle(isNearby), GUILayout.Width(halfWidth)) && !isNearby)
		{
			_ctrl.combatScanMode = AutoController.CombatScanMode.Nearby;
			_ctrl.SaveConfig();
		}
		GUILayout.EndHorizontal();

		if (isNearby)
		{
			GUILayout.Space(4f);
			GUILayout.Label($"侦测半径: {_ctrl.CombatScanRadius} 格（约 {_ctrl.CombatScanRadius * 2}×{_ctrl.CombatScanRadius * 2} 范围）", _labelStyle);
			int radius = (int)GUILayout.HorizontalSlider((float)_ctrl.CombatScanRadius, 3f, 40f);
			if (radius != _ctrl.CombatScanRadius)
			{
				_ctrl.CombatScanRadius = radius;
				_ctrl.SaveConfig();
			}
			GUILayout.Label("常规采集时在此半径内索敌；当角色空闲（近处采完/打完）时仍会自动全图扫描残余怪与掉落物。", _labelSubStyle);
		}
		else
		{
			GUILayout.Label("全图扫描可行动范围内所有敌对生物并主动走过去战斗。", _labelSubStyle);
		}

		GUILayout.Space(8f);
		DrawSeparator();
		GUILayout.Space(6f);

		DrawCombatAndExplorePriorityControls(contentWidth);
	}

	/// <summary>负重上限/超量 N% 自动停止控件。</summary>
	private void DrawBurdenStopControls()
	{
		bool nextStopBurden = GUILayout.Toggle(_ctrl.StopOnBurdenEnabled, " ⚖️ 达到负重上限/超量 N% 后自动停止脚本", _toggleStyle);
		if (nextStopBurden != _ctrl.StopOnBurdenEnabled)
		{
			_ctrl.StopOnBurdenEnabled = nextStopBurden;
			_ctrl.SaveConfig();
		}
		if (_ctrl.StopOnBurdenEnabled)
		{
			int overPct = _ctrl.MaxBurdenPercent - 100;
			string overStr = (overPct >= 0) ? $"超量 +{overPct}%" : $"上限 {overPct}%";
			int curBurden = _ctrl.GetCurrentBurdenPercent(EClass.pc);

			GUILayout.BeginHorizontal();
			GUILayout.Label($"停止阈值: {_ctrl.MaxBurdenPercent}%（{overStr} · 当前:{curBurden}%）", _labelStyle);
			GUILayout.FlexibleSpace();
			GUILayout.Label("数值%:", _labelSubStyle, GUILayout.Width(40f));
			GUI.SetNextControlName("burdenPctField");
			string sBurden = GUILayout.TextField(_ctrl.MaxBurdenPercent.ToString(), GUILayout.Width(44f));
			GUILayout.EndHorizontal();

			if (int.TryParse(sBurden, out var parsedBurden))
			{
				int clampedBurden = Mathf.Clamp(parsedBurden, 50, 300);
				if (clampedBurden != _ctrl.MaxBurdenPercent)
				{
					_ctrl.MaxBurdenPercent = clampedBurden;
					_ctrl.SaveConfig();
				}
			}

			int sliderBurden = (int)GUILayout.HorizontalSlider((float)_ctrl.MaxBurdenPercent, 50f, 300f);
			if (sliderBurden != _ctrl.MaxBurdenPercent)
			{
				_ctrl.MaxBurdenPercent = sliderBurden;
				_ctrl.SaveConfig();
			}
			GUILayout.Label("说明：100% = 刚好达到负重上限(超量0%)；120% = 超量20%后自动停止脚本。", _labelSubStyle);
		}
	}

	private string FormatSlotBadge(string name, int slot)
	{
		return (slot >= 0) ? $"[{name}:{slot + 1}]" : $"[{name}:未设]";
	}

	private string FormatDualSlotBadge(string name, int primarySlot, int secondarySlot)
	{
		int effective = (primarySlot >= 0) ? primarySlot : secondarySlot;
		return (effective >= 0) ? $"[{name}:{effective + 1}]" : $"[{name}:未设]";
	}

	private bool DrawToggleWithBadge(bool value, string label, string badge)
	{
		GUILayout.BeginHorizontal();
		bool next = GUILayout.Toggle(value, label, _toggleStyle);
		GUILayout.FlexibleSpace();
		if (!string.IsNullOrEmpty(badge))
		{
			GUILayout.Label(badge, _labelSubStyle);
		}
		GUILayout.EndHorizontal();
		return next;
	}

	/// <summary>打怪目标优先级（近处优先 vs 血少优先）与全图行动优先级（怪优先 vs 捡优先）公共控件（统一归入【基础设置 -> 阈值与索敌】，不再在采集页下方重复出现）。</summary>
	private void DrawCombatAndExplorePriorityControls(float contentWidth)
	{
		float halfW = (contentWidth - 12f) / 2f;

		GUILayout.Label("打怪目标选择（全局通用 · 默认优先打近处的怪）", _labelStyle);
		GUILayout.BeginHorizontal();
		bool isNearestTarget = _ctrl.combatTargetPriority == AutoController.CombatTargetPriority.Nearest;
		bool isLowestHpTarget = _ctrl.combatTargetPriority == AutoController.CombatTargetPriority.LowestHp;
		if (GUILayout.Button("📍 优先打近处 (默认)", MakeSelectableBtnStyle(isNearestTarget), GUILayout.Width(halfW), GUILayout.Height(26f)) && !isNearestTarget)
		{
			_ctrl.combatTargetPriority = AutoController.CombatTargetPriority.Nearest;
			_ctrl.SaveConfig();
		}
		if (GUILayout.Button("🩸 优先打血少 (残血)", MakeSelectableBtnStyle(isLowestHpTarget), GUILayout.Width(halfW), GUILayout.Height(26f)) && !isLowestHpTarget)
		{
			_ctrl.combatTargetPriority = AutoController.CombatTargetPriority.LowestHp;
			_ctrl.SaveConfig();
		}
		GUILayout.EndHorizontal();
		GUILayout.Label(isNearestTarget
			? "当前：优先攻击距离玩家最近的可达怪物。"
			: "当前：优先攻击当前生命值(HP)最少的可达怪物（同血量时优先近处）。", _labelSubStyle);

		GUILayout.Space(6f);

		GUILayout.Label("全图扫怪与拾取优先级（全局通用 · 近处有怪始终优先自卫打怪）", _labelStyle);
		GUILayout.BeginHorizontal();
		bool isMonsterFirst = _ctrl.explorePriority == AutoController.ExploreActionPriority.MonsterFirst;
		bool isPickupFirst = _ctrl.explorePriority == AutoController.ExploreActionPriority.PickupFirst;
		if (GUILayout.Button("⚔️ 怪优先", MakeSelectableBtnStyle(isMonsterFirst), GUILayout.Width(halfW), GUILayout.Height(26f)) && !isMonsterFirst)
		{
			_ctrl.explorePriority = AutoController.ExploreActionPriority.MonsterFirst;
			_ctrl.SaveConfig();
		}
		if (GUILayout.Button("🎒 捡优先", MakeSelectableBtnStyle(isPickupFirst), GUILayout.Width(halfW), GUILayout.Height(26f)) && !isPickupFirst)
		{
			_ctrl.explorePriority = AutoController.ExploreActionPriority.PickupFirst;
			_ctrl.SaveConfig();
		}
		GUILayout.EndHorizontal();
		GUILayout.Label(isMonsterFirst
			? "当前【怪优先】：近处打完后，先全图扫描找怪走过去打，打完再全图走过去捡掉落物。"
			: "当前【捡优先】：近处打完后，先全图扫描地面可拾取物走过去捡（途中若旁边有怪靠近会自动先打近处怪），捡完再全图找怪。", _labelSubStyle);
	}

	private void DrawGatherTab(float contentWidth)
	{
		// ---------- 1. 顶部：通用采集规则（专项总开关 + 自动拾取 + 采集先后顺序） ----------
		GUILayout.Label("【通用采集规则（对野外 / 地牢 / 临时模块全部生效）】", _fieldLabelStyle);
		GUILayout.Space(2f);

		GUILayout.BeginHorizontal();
		bool nextTargetMode = GUILayout.Toggle(_ctrl.TargetedGatherEnabled, " 🎯 启用单独/少量材料收集（总开关）", _toggleStyle);
		if (nextTargetMode != _ctrl.TargetedGatherEnabled)
		{
			_ctrl.TargetedGatherEnabled = nextTargetMode;
			_ctrl.SaveConfig();
		}
		GUILayout.FlexibleSpace();
		if (GUILayout.Button(_showTargetedSubWindow ? $"收起专项窗口({_ctrl.TargetedGatherKeys.Count})" : $"打开专项窗口({_ctrl.TargetedGatherKeys.Count}) ↗", MakeSelectableBtnStyle(_showTargetedSubWindow), GUILayout.Width(138f), GUILayout.Height(22f)))
		{
			if (_showTargetedSubWindow)
			{
				_showTargetedSubWindow = false;
			}
			else
			{
				OpenTargetedSubWindow();
			}
		}
		GUILayout.EndHorizontal();

		string orderDesc = (_ctrl.gatherOrderMode == AutoController.GatherOrderMode.NearestFirst) ? "就近采集" : "种子 → 采集 → 挖矿";
		GUILayout.Label(_ctrl.TargetedGatherEnabled
			? $"当前已开启【专项材料收集】（预设: {_ctrl.ActivePresetName}）：只采集专项窗口中勾选的目标（按「{orderDesc}」执行，可叠加临时挖墙）。"
			: $"当前未勾选【专项材料收集】：按默认全部采集规则运行（自动识别野外/地牢环境，按「{orderDesc}」执行）。", _labelSubStyle);

		GUILayout.Space(4f);
		bool autoPick = GUILayout.Toggle(_ctrl.autoPickEnabled, " 🎒 自动拾取地面掉落物（全图扫描并走过去拾取；地牢环境默认自动开启）", _toggleStyle);
		if (autoPick != _ctrl.autoPickEnabled)
		{
			_ctrl.autoPickEnabled = autoPick;
			_ctrl.SaveConfig();
		}

		GUILayout.Space(6f);
		GUILayout.Label("采集先后顺序（默认就近采集 · 地牢敲普通墙/临时挖墙排在普通地面采集物之后）", _fieldLabelStyle);
		GUILayout.BeginHorizontal();
		bool isNearestOrder = _ctrl.gatherOrderMode == AutoController.GatherOrderMode.NearestFirst;
		bool isCategoryOrder = _ctrl.gatherOrderMode == AutoController.GatherOrderMode.CategoryOrder;
		float halfOrderW = (contentWidth - 12f) / 2f;
		if (GUILayout.Button("🏃 就近采集 (默认)", MakeSelectableBtnStyle(isNearestOrder), GUILayout.Width(halfOrderW), GUILayout.Height(26f)) && !isNearestOrder)
		{
			_ctrl.gatherOrderMode = AutoController.GatherOrderMode.NearestFirst;
			_ctrl.SaveConfig();
		}
		if (GUILayout.Button("📋 种子→采集→挖矿", MakeSelectableBtnStyle(isCategoryOrder), GUILayout.Width(halfOrderW), GUILayout.Height(26f)) && !isCategoryOrder)
		{
			_ctrl.gatherOrderMode = AutoController.GatherOrderMode.CategoryOrder;
			_ctrl.SaveConfig();
		}
		GUILayout.EndHorizontal();
		GUILayout.Label(isNearestOrder
			? "当前：就近采集（省脚步就是省生命；种子、采集物、矿石谁离得近先采谁，同格内成熟植物先收种）。"
			: "当前：种子 → 采集 → 挖矿（减少切换工具）。", _labelSubStyle);

		GUILayout.Space(8f);
		DrawSeparator();
		GUILayout.Space(6f);

		// ---------- 2. 分环境设置模块（野外设置 / 地牢设置 / 临时模块，再次点击当前已展开按钮可折叠收起） ----------
		GUILayout.Label("【分环境采集模块（再次点击已展开的分页按钮可收起面板）】", _fieldLabelStyle);
		bool isField = _ctrl.gatherCategory == AutoController.GatherCategory.Field;
		bool isDungeon = _ctrl.gatherCategory == AutoController.GatherCategory.Dungeon;
		bool isTemp = _ctrl.gatherCategory == AutoController.GatherCategory.Temp;

		GUILayout.BeginHorizontal();
		float third = (contentWidth - 16f) / 3f;

		string fieldBtnTitle = (isField ? "▼ " : "▶ ") + "🌿 野外设置";
		if (GUILayout.Button(fieldBtnTitle, MakeSelectableBtnStyle(isField), GUILayout.Width(third), GUILayout.Height(28f)))
		{
			_ctrl.gatherCategory = isField ? AutoController.GatherCategory.None : AutoController.GatherCategory.Field;
			_ctrl.SaveConfig();
		}

		string dungeonBtnTitle = (isDungeon ? "▼ " : "▶ ") + "🏰 地牢设置";
		if (GUILayout.Button(dungeonBtnTitle, MakeSelectableBtnStyle(isDungeon), GUILayout.Width(third), GUILayout.Height(28f)))
		{
			_ctrl.gatherCategory = isDungeon ? AutoController.GatherCategory.None : AutoController.GatherCategory.Dungeon;
			_ctrl.SaveConfig();
		}

		string tempCoreTitle = _ctrl.HasAnyTempWallEnabled() ? "⚡ 临时(已开)" : "⚡ 临时模块";
		string tempBtnTitle = (isTemp ? "▼ " : "▶ ") + tempCoreTitle;
		if (GUILayout.Button(tempBtnTitle, MakeSelectableBtnStyle(isTemp || _ctrl.HasAnyTempWallEnabled()), GUILayout.Width(third), GUILayout.Height(28f)))
		{
			_ctrl.gatherCategory = isTemp ? AutoController.GatherCategory.None : AutoController.GatherCategory.Temp;
		}
		GUILayout.EndHorizontal();

		if (_ctrl.gatherCategory == AutoController.GatherCategory.None)
		{
			GUILayout.Space(4f);
			GUILayout.Label("（当前已收起分环境详细面板；点击上方【🌿 野外设置 / 🏰 地牢设置 / ⚡ 临时模块】可展开对应配置）", _labelSubStyle);
		}
		else
		{
			GUILayout.Space(6f);
			GUILayout.BeginVertical(_boxStyle);
			if (isField)
			{
				GUILayout.Label((_ctrl.gatherOrderMode == AutoController.GatherOrderMode.NearestFirst)
					? "野外采集项（非地牢环境自动启用 · 当前按距离就近采集）"
					: "野外采集项（非地牢环境自动启用 · 当前顺序：种子 → 采集 → 挖矿（减少切换工具））", _fieldLabelStyle);
				GUILayout.Space(2f);

				bool nextCrops = DrawToggleWithBadge(_ctrl.FieldCrops, " 🌾 [种子] 采作物/收种子（成熟作物与植物收割种子）", FormatSlotBadge("镰刀槽", _ctrl.SickleSlot));
				if (nextCrops != _ctrl.FieldCrops)
				{
					_ctrl.FieldCrops = nextCrops;
					_ctrl.SaveConfig();
				}

				bool nextChop = DrawToggleWithBadge(_ctrl.FieldChop, " 🪓 [采集] 砍树与杂物（树木 / 藤蔓 / 草药 / 蜘蛛网）", FormatSlotBadge("砍树槽", _ctrl.ChoppingSlot));
				if (nextChop != _ctrl.FieldChop)
				{
					_ctrl.FieldChop = nextChop;
					_ctrl.SaveConfig();
				}

				bool nextMine = DrawToggleWithBadge(_ctrl.FieldMine, " ⛏️ [挖矿] 野外挖矿（盐矿 / 矿石 / 晶簇 / 岩石，不挖普通墙）", FormatSlotBadge("挖矿槽", _ctrl.MiningSlot));
				if (nextMine != _ctrl.FieldMine)
				{
					_ctrl.FieldMine = nextMine;
					_ctrl.SaveConfig();
				}

				GUILayout.Space(6f);
				DrawSeparator();
				GUILayout.Space(4f);

				GUILayout.Label("深水区与游泳设置（默认避开深水防淹死）", _fieldLabelStyle);
				bool nextIgnoreDeep = GUILayout.Toggle(_ctrl.IgnoreDeepWater, " \ud83e\udd3f 直接无视深水区（已备潜水道具/水下呼吸，自由踏入深水）", _toggleStyle);
				if (nextIgnoreDeep != _ctrl.IgnoreDeepWater)
				{
					_ctrl.IgnoreDeepWater = nextIgnoreDeep;
					if (_ctrl.IgnoreDeepWater)
					{
						_ctrl.SwimModeEnabled = false;
					}
					_ctrl.SaveConfig();
				}

				bool nextSwim = GUILayout.Toggle(_ctrl.SwimModeEnabled, " \ud83c\udfca 游泳模式（允许踏入深水区，检测氧气量与上岸换气）", _toggleStyle);
				if (nextSwim != _ctrl.SwimModeEnabled)
				{
					_ctrl.SwimModeEnabled = nextSwim;
					if (_ctrl.SwimModeEnabled)
					{
						_ctrl.IgnoreDeepWater = false;
					}
					_ctrl.SaveConfig();
				}

				if (_ctrl.SwimModeEnabled && !_ctrl.IgnoreDeepWater)
				{
					int curOxy = _ctrl.GetCurrentOxygenPercent(EClass.pc);
					GUILayout.Label($"氧气上岸阈值: {_ctrl.SwimRetreatOxygenPercent}%（当前氧气: {curOxy}%）", _labelStyle);
					int oxySlider = (int)GUILayout.HorizontalSlider((float)_ctrl.SwimRetreatOxygenPercent, 20f, 85f);
					if (oxySlider != _ctrl.SwimRetreatOxygenPercent)
					{
						_ctrl.SwimRetreatOxygenPercent = oxySlider;
						_ctrl.SaveConfig();
					}
					GUILayout.Label("允许进入深水行动；氧气降至阈值时立即游回最近岸上恢复至 100% 后继续。", _labelSubStyle);
				}
				else if (_ctrl.IgnoreDeepWater)
				{
					GUILayout.Label("已开启无视深水区：深水格视同平地自由通行与采集，不触发溺水上岸。", _labelSubStyle);
				}
				else
				{
					GUILayout.Label("默认避开深水区（浅水与盐矿可正常采集）；万一拐角误入深水立即自动退回岸上。", _labelSubStyle);
				}
			}
			else if (isDungeon)
			{
				GUILayout.Label((_ctrl.gatherOrderMode == AutoController.GatherOrderMode.NearestFirst)
					? "地牢采集项（地牢环境自动启用 · 当前按距离就近采集，敲墙排在地面采集物之后）"
					: "地牢采集项（地牢环境自动启用 · 当前顺序：种子 → 采集 → 挖矿（减少切换工具））", _fieldLabelStyle);
				GUILayout.Space(2f);

				bool nextDCrops = DrawToggleWithBadge(_ctrl.DungeonCrops, " 🌾 [种子] 采作物/收种子（地牢内成熟植物与菌类收割种子）", FormatSlotBadge("镰刀槽", _ctrl.SickleSlot));
				if (nextDCrops != _ctrl.DungeonCrops)
				{
					_ctrl.DungeonCrops = nextDCrops;
					_ctrl.SaveConfig();
				}

				bool nextDChop = DrawToggleWithBadge(_ctrl.DungeonChop, " 🪓 [采集] 砍伐与杂物（树木 / 藤蔓 / 草药 / 蜘蛛网 / 骸骨残骸）", FormatDualSlotBadge("砍树/挖矿", _ctrl.ChoppingSlot, _ctrl.MiningSlot));
				if (nextDChop != _ctrl.DungeonChop)
				{
					_ctrl.DungeonChop = nextDChop;
					_ctrl.SaveConfig();
				}

				bool nextDMine = DrawToggleWithBadge(_ctrl.DungeonMineObjs, " ⛏️ [挖矿] 采矿石与附着物（矿脉 / 晶簇 / 钟乳石 / 墙面铁皮，不挖墙）", FormatSlotBadge("挖矿槽", _ctrl.MiningSlot));
				if (nextDMine != _ctrl.DungeonMineObjs)
				{
					_ctrl.DungeonMineObjs = nextDMine;
					_ctrl.SaveConfig();
				}

				GUILayout.Space(6f);
				DrawSeparator();
				GUILayout.Space(6f);

				GUILayout.Label("地牢自动敲墙与负重停止（同级联动控制）", _fieldLabelStyle);
				bool nextMineWalls = DrawToggleWithBadge(_ctrl.DungeonMineWalls, " 🧱 地牢内自动敲墙（按墙体材质自动切挖矿/砍树工具）", FormatDualSlotBadge("挖矿/砍树", _ctrl.MiningSlot, _ctrl.ChoppingSlot));
				if (nextMineWalls != _ctrl.DungeonMineWalls)
				{
					_ctrl.DungeonMineWalls = nextMineWalls;
					_ctrl.SaveConfig();
				}

				if (_ctrl.DungeonMineWalls)
				{
					GUILayout.BeginHorizontal();
					GUILayout.Label("敲墙上限(0=不限):", _labelStyle, GUILayout.Width(114f));
					GUI.SetNextControlName("wallLimitField");
					string sWallLimit = GUILayout.TextField(_ctrl.DungeonMineWallLimit.ToString(), GUILayout.Width(48f));
					GUILayout.Space(4f);
					GUILayout.Label($"个 · 本次已敲: {_ctrl.DungeonMinedWallCount}", _labelSubStyle);
					GUILayout.FlexibleSpace();
					if (GUILayout.Button("清零", _btnStyle, GUILayout.Width(46f), GUILayout.Height(20f)))
					{
						_ctrl.ResetDungeonMinedWallCount();
					}
					GUILayout.EndHorizontal();

					if (int.TryParse(sWallLimit, out var parsedWallLimit))
					{
						int clampedWallLimit = Mathf.Clamp(parsedWallLimit, 0, 500);
						if (clampedWallLimit != _ctrl.DungeonMineWallLimit)
						{
							_ctrl.DungeonMineWallLimit = clampedWallLimit;
							_ctrl.SaveConfig();
						}
					}

					int sliderWallLimit = (int)GUILayout.HorizontalSlider((float)_ctrl.DungeonMineWallLimit, 0f, 500f);
					if (sliderWallLimit != _ctrl.DungeonMineWallLimit)
					{
						_ctrl.DungeonMineWallLimit = sliderWallLimit;
						_ctrl.SaveConfig();
					}
					GUILayout.Label((_ctrl.DungeonMineWallLimit > 0) ? $"敲满 {_ctrl.DungeonMineWallLimit} 个墙后将自动停止脚本。" : "当前设为 0（不限制敲墙个数，仅受体力与下方负重阈值控制）。", _labelSubStyle);
				}

				GUILayout.Space(4f);
				DrawBurdenStopControls();

				GUILayout.Space(6f);
				DrawSeparator();
				GUILayout.Space(6f);

				// 地面陷阱处理区块
				GUILayout.Label("地面陷阱处理（全程寻路与落脚避开所有可见及隐藏陷阱）", _fieldLabelStyle);
				bool nextAvoidTraps = GUILayout.Toggle(_ctrl.AvoidTraps, " ⚠️ 行走避开所有地面陷阱（含肉眼看不见的隐藏陷阱）", _toggleStyle);
				if (nextAvoidTraps != _ctrl.AvoidTraps)
				{
					_ctrl.AvoidTraps = nextAvoidTraps;
					_ctrl.SaveConfig();
				}
				bool nextDisarmTraps = GUILayout.Toggle(_ctrl.DisarmTraps, " 🔧 尝试拆除陷阱（站在安全邻格尝试拆除已发现陷阱，失败自动绕开）", _toggleStyle);
				if (nextDisarmTraps != _ctrl.DisarmTraps)
				{
					_ctrl.DisarmTraps = nextDisarmTraps;
					_ctrl.SaveConfig();
				}
			}
			else if (isTemp)
			{
				GUILayout.Label("临时挖墙模块（独立附加模块 · 勾选后排在普通地面采集物之后执行）", _fieldLabelStyle);
				GUILayout.Label("说明：停留在本页或切回其他设置页均不影响常规采集；停止运行或切换预设时自动归零防拆家。", _labelSubStyle);
				GUILayout.Space(2f);

				_ctrl.TempMineSoil = DrawToggleWithBadge(_ctrl.TempMineSoil, " 🟫 单一挖土墙（泥土 / 黏土 / 沙土墙 · soil/sand）", FormatSlotBadge("挖矿槽", _ctrl.MiningSlot));
				_ctrl.TempMineRock = DrawToggleWithBadge(_ctrl.TempMineRock, " 🪨 单一挖石墙（岩石 / 花岗岩等石墙 · rock）", FormatSlotBadge("挖矿槽", _ctrl.MiningSlot));
				_ctrl.TempMineOre = DrawToggleWithBadge(_ctrl.TempMineOre, " 🔩 单一挖铁墙（铁 / 钢 / 铜等金属墙 · ore）", FormatSlotBadge("挖矿槽", _ctrl.MiningSlot));
				_ctrl.TempMineWoodGrass = DrawToggleWithBadge(_ctrl.TempMineWoodGrass, " 🌿 单一挖草/木墙（木墙 / 草墙 · wood/grass）", FormatDualSlotBadge("砍树/挖矿", _ctrl.ChoppingSlot, _ctrl.MiningSlot));
				_ctrl.TempMineAllWalls = DrawToggleWithBadge(_ctrl.TempMineAllWalls, " 💥 挖全部墙体（无差别挖掘范围内所有墙体）", FormatDualSlotBadge("挖矿/砍树", _ctrl.MiningSlot, _ctrl.ChoppingSlot));

				GUILayout.Space(4f);
				if (_ctrl.HasAnyTempWallEnabled())
				{
					GUILayout.BeginHorizontal();
					GUILayout.Label("⚠ 已开启临时挖墙模块（排在普通地面采集物之后执行；停止运行时自动全关）", _labelSubStyle);
					GUILayout.FlexibleSpace();
					if (GUILayout.Button("全部关闭", _btnStyle, GUILayout.Width(72f), GUILayout.Height(22f)))
					{
						_ctrl.ResetTempWallOptions();
					}
					GUILayout.EndHorizontal();
				}
				else
				{
					GUILayout.Label("当前临时挖墙全部关闭（此时点运行将正常执行常规地面采集）。", _labelSubStyle);
				}
			}
			GUILayout.EndVertical();
		}
	}

	/// <summary>单独（或少量）材料收集专用页面：可按当前地图实况、种子、树木、采集物（如蜘蛛网）、矿石（如铜矿）勾选保存，受总开关控制。</summary>
	private void DrawTargetedTab(float contentWidth)
	{
		_ctrl.UpgradeLegacyTargetedGatherKeysIfReady();
		GUILayout.BeginHorizontal();
		bool nextEnabled = GUILayout.Toggle(_ctrl.TargetedGatherEnabled, " 🎯 启用单独/少量材料收集（总开关）", _toggleStyle);
		if (nextEnabled != _ctrl.TargetedGatherEnabled)
		{
			_ctrl.TargetedGatherEnabled = nextEnabled;
			_ctrl.SaveConfig();
		}
		GUILayout.EndHorizontal();

		string tOrderStr = (_ctrl.gatherOrderMode == AutoController.GatherOrderMode.NearestFirst) ? "就近采集" : "种子 → 采集 → 挖矿";
		GUILayout.Label(_ctrl.TargetedGatherEnabled
			? $"● 当前状态：【已启用】— 运行时只收集下方已选材料（当前按「{tOrderStr}」执行）；状态栏也可随时切换预设与开关。"
			: "○ 当前状态：【未启用】— 运行时按默认全部采集算；勾选上方总开关（或点状态栏的🎯专项按钮）即可启用。", _labelSubStyle);

		GUILayout.Space(6f);
		DrawSeparator();
		GUILayout.Space(4f);

		// ---------- 专项预设下拉栏（酒馆式拉取下拉框 + 保存 + 新增） ----------
		DrawTargetedPresetBar(contentWidth);

		GUILayout.Space(6f);
		DrawSeparator();
		GUILayout.Space(4f);

		// ---------- 已选收集目标列表 ----------
		GUILayout.BeginHorizontal();
		GUILayout.Label($"已选收集目标 ({_ctrl.TargetedGatherKeys.Count} 项) · 点击条目右侧 × 可移除：", _fieldLabelStyle);
		GUILayout.FlexibleSpace();
		if (_ctrl.TargetedGatherKeys.Count > 0 && GUILayout.Button("清空已选", _btnStyle, GUILayout.Width(68f), GUILayout.Height(20f)))
		{
			_ctrl.ClearTargetedGatherKeys();
		}
		GUILayout.EndHorizontal();
		if (_ctrl.TargetedGatherKeys.Count == 0)
		{
			GUILayout.Label("（暂未选择任何目标，请在下方【📍当前图】或各分类中点击勾选）", _labelSubStyle);
		}
		else
		{
			List<string> selectedKeys = _ctrl.TargetedGatherKeys.ToList();
			selectedKeys.Sort(StringComparer.OrdinalIgnoreCase);
			float selectedBoxHeight = Mathf.Clamp((float)selectedKeys.Count * 24f + 8f, 34f, 110f);
			_targetedSelectedScroll = GUILayout.BeginScrollView(_targetedSelectedScroll, GUILayout.Height(selectedBoxHeight));
			for (int i = 0; i < selectedKeys.Count; i++)
			{
				string k = selectedKeys[i];
				string disp = _ctrl.GetTargetKeyDisplayLabel(k);
				GUILayout.BeginHorizontal();
				GUILayout.Label("● " + disp, _labelStyle);
				GUILayout.FlexibleSpace();
				if (GUILayout.Button("× 移除", _btnStyle, GUILayout.Width(54f), GUILayout.Height(20f)))
				{
					_ctrl.RemoveTargetKey(k);
				}
				GUILayout.EndHorizontal();
			}
			GUILayout.EndScrollView();
		}

		GUILayout.Space(6f);
		DrawSeparator();
		GUILayout.Space(4f);

		// ---------- 候选分类导航栏 ----------
		GUILayout.Label("选择要收集的目标（同名/同类已一键全包 · 勾选一次跨所有地图永久通用）：", _fieldLabelStyle);
		GUILayout.BeginHorizontal();
		float subBtnW = (contentWidth - 24f) / 5f;
		DrawTargetedSubTabButton(TargetedSubTab.CurrentMap, "📍当前图", subBtnW);
		DrawTargetedSubTabButton(TargetedSubTab.Seed, "🌾种子", subBtnW);
		DrawTargetedSubTabButton(TargetedSubTab.Tree, "🪓树木", subBtnW);
		DrawTargetedSubTabButton(TargetedSubTab.Gather, "🕸️采集", subBtnW);
		DrawTargetedSubTabButton(TargetedSubTab.Mine, "⛏️矿石", subBtnW);
		GUILayout.EndHorizontal();

		GUILayout.Space(4f);
		// ---------- 搜索过滤栏 + 刷新按钮 ----------
		GUILayout.BeginHorizontal();
		GUILayout.Label("🔍 搜索:", _labelStyle, GUILayout.Width(52f));
		GUI.SetNextControlName("targetedSearchField");
		_targetedSearchText = GUILayout.TextField(_targetedSearchText ?? "", GUILayout.ExpandWidth(true));
		if (GUILayout.Button("粘贴", _btnStyle, GUILayout.Width(38f), GUILayout.Height(20f)))
		{
			string clip = GUIUtility.systemCopyBuffer;
			if (!string.IsNullOrEmpty(clip))
			{
				_targetedSearchText = clip.Replace("\r", "").Replace("\n", "").Trim();
			}
			GUI.FocusControl("targetedSearchField");
		}
		if (!string.IsNullOrEmpty(_targetedSearchText) && GUILayout.Button("清除", _btnStyle, GUILayout.Width(42f), GUILayout.Height(20f)))
		{
			_targetedSearchText = "";
		}
		if (_targetedSubTab == TargetedSubTab.CurrentMap && GUILayout.Button("🔄 刷新", _btnStyle, GUILayout.Width(56f), GUILayout.Height(20f)))
		{
			RefreshTargetedCatalog(force: true);
		}
		GUILayout.EndHorizontal();

		RefreshTargetedCatalog(force: false);

		string filter = (_targetedSearchText ?? "").Trim();
		GUILayout.Space(4f);

		float catalogHeight = Mathf.Max(180f, _targetedWindowRect.height - 410f);
		_targetedCatalogScroll = GUILayout.BeginScrollView(_targetedCatalogScroll, GUILayout.Height(catalogHeight));
		int shownCount = 0;
		for (int i = 0; i < _cachedCatalog.Count; i++)
		{
			AutoController.CatalogEntry entry = _cachedCatalog[i];
			if (!string.IsNullOrEmpty(filter))
			{
				if (entry.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && entry.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}
			}
			shownCount++;
			bool isSelected = _ctrl.IsTargetKeySelected(entry.Key);
			string itemText = (entry.MapCount > 0) ? $" {entry.Label}  [图内:{entry.MapCount}]" : $" {entry.Label}";
			bool nextSelected = GUILayout.Toggle(isSelected, itemText, _toggleStyle);
			if (nextSelected != isSelected)
			{
				_ctrl.ToggleTargetKey(entry.Key, entry.Label);
			}
		}
		if (shownCount == 0)
		{
			if (_targetedSubTab == TargetedSubTab.CurrentMap && EClass._map == null)
			{
				GUILayout.Label("尚未进入地图，请进入地图后查看【📍当前图】，或直接切换到右侧【🌾种子 / 🪓树木 / 🕸️采集 / ⛏️矿石】从全游戏词条库勾选。", _labelSubStyle);
			}
			else
			{
				GUILayout.Label("没有匹配的可采集条目。", _labelSubStyle);
			}
		}
		GUILayout.EndScrollView();
	}

	private void DrawTargetedSubTabButton(TargetedSubTab subTab, string label, float width)
	{
		bool active = _targetedSubTab == subTab;
		if (GUILayout.Button(label, MakeSelectableBtnStyle(active), GUILayout.Width(width), GUILayout.Height(24f)) && !active)
		{
			_targetedSubTab = subTab;
			RefreshTargetedCatalog(force: true);
		}
	}

	private void RefreshTargetedCatalog(bool force)
	{
		int dbCount = ((Cell.objList != null) ? Cell.objList.Count : 0) + ((Cell.matList != null) ? Cell.matList.Count : 0) + ((EClass.sources?.things?.rows != null) ? EClass.sources.things.rows.Count : 0);
		if (!force && _cachedCatalogTab == _targetedSubTab && _cachedCatalogMap == EClass._map && _cachedCatalogDbCount == dbCount)
		{
			if (_targetedSubTab != TargetedSubTab.CurrentMap || Time.realtimeSinceStartup - _lastCatalogScanTime < 2.0f)
			{
				return;
			}
		}
		_cachedCatalogTab = _targetedSubTab;
		_cachedCatalogMap = EClass._map;
		_cachedCatalogDbCount = dbCount;
		_lastCatalogScanTime = Time.realtimeSinceStartup;

		switch (_targetedSubTab)
		{
		case TargetedSubTab.CurrentMap:
			_cachedCatalog = _ctrl.ScanCurrentMapGatherCatalog();
			break;
		case TargetedSubTab.Seed:
			_cachedCatalog = _ctrl.GetDatabaseCatalogByCategory(AutoController.TargetItemCategory.Seed);
			break;
		case TargetedSubTab.Tree:
			_cachedCatalog = _ctrl.GetDatabaseCatalogByCategory(AutoController.TargetItemCategory.Tree);
			break;
		case TargetedSubTab.Gather:
			_cachedCatalog = _ctrl.GetDatabaseCatalogByCategory(AutoController.TargetItemCategory.Gather);
			break;
		case TargetedSubTab.Mine:
			_cachedCatalog = _ctrl.GetDatabaseCatalogByCategory(AutoController.TargetItemCategory.Mine);
			break;
		}
	}

	private void DrawMoreTab(float contentWidth)
	{

		bool debugMode = GUILayout.Toggle(_ctrl.DebugMode, " 调试日志", _toggleStyle);
		GUILayout.Label("写 debug.log，排查问题用", _labelSubStyle);
		if (debugMode != _ctrl.DebugMode)
		{
			_ctrl.DebugMode = debugMode;
			_ctrl.SaveConfig();
		}

		GUILayout.Space(10f);
		DrawSeparator();
		GUILayout.Space(6f);

		GUILayout.Label("可行动范围检测与子区域设置", _fieldLabelStyle);
		if (_ctrl.TryGetMapPlayableBounds(out var bMinX, out var bMaxX, out var bMinZ, out var bMaxZ))
		{
			GUILayout.Label($"当前地图真实可行动边界 (map.bounds): X[{bMinX}–{bMaxX}] · Z[{bMinZ}–{bMaxZ}]", _labelStyle);
		}
		else
		{
			GUILayout.Label("当前地图真实可行动边界 (map.bounds): 未进入地图", _labelSubStyle);
		}

		bool nextCustom = GUILayout.Toggle(_ctrl.UseCustomSearchBounds, " 📐 手动限定更小的子区域（关闭=自动跟随当前地图真实可行动边界）", _toggleStyle);
		if (nextCustom != _ctrl.UseCustomSearchBounds)
		{
			_ctrl.UseCustomSearchBounds = nextCustom;
			_ctrl.SaveConfig();
		}
		GUILayout.Label(_ctrl.UseCustomSearchBounds ? "当前使用下方自定义框（与 map.bounds 取交集，绝不越界）：" : "默认自动读取 EClass._map.bounds：普通野外自动为 36–64 防卡边缘，家园扩建或大海滩地图自动同步扩大后的边界！", _labelSubStyle);

		GUILayout.Space(4f);
		GUILayout.BeginHorizontal();
		GUILayout.Label("X 最小:", _labelStyle, GUILayout.Width(50f));
		GUI.SetNextControlName("minXField");
		string sMinX = GUILayout.TextField(_ctrl.SearchMinX.ToString(), GUILayout.Width(48f));
		GUILayout.Space(6f);
		GUILayout.Label("X 最大:", _labelStyle, GUILayout.Width(50f));
		GUI.SetNextControlName("maxXField");
		string sMaxX = GUILayout.TextField(_ctrl.SearchMaxX.ToString(), GUILayout.Width(48f));
		GUILayout.FlexibleSpace();
		if (GUILayout.Button("读取当前图边界", _btnStyle, GUILayout.Width(104f), GUILayout.Height(22f)))
		{
			_ctrl.SyncCustomBoundsToCurrentMap();
		}
		GUILayout.EndHorizontal();

		if (int.TryParse(sMinX, out var minXResult) && minXResult >= 0 && minXResult <= 250 && minXResult != _ctrl.SearchMinX)
		{
			_ctrl.SearchMinX = minXResult;
			_ctrl.SaveConfig();
		}
		if (int.TryParse(sMaxX, out var maxXResult) && maxXResult >= 0 && maxXResult <= 250 && maxXResult != _ctrl.SearchMaxX)
		{
			_ctrl.SearchMaxX = maxXResult;
			_ctrl.SaveConfig();
		}

		GUILayout.Space(4f);
		GUILayout.BeginHorizontal();
		GUILayout.Label("Z 最小:", _labelStyle, GUILayout.Width(50f));
		GUI.SetNextControlName("minZField");
		string sMinZ = GUILayout.TextField(_ctrl.SearchMinZ.ToString(), GUILayout.Width(48f));
		GUILayout.Space(6f);
		GUILayout.Label("Z 最大:", _labelStyle, GUILayout.Width(50f));
		GUI.SetNextControlName("maxZField");
		string sMaxZ = GUILayout.TextField(_ctrl.SearchMaxZ.ToString(), GUILayout.Width(48f));
		GUILayout.EndHorizontal();

		if (int.TryParse(sMinZ, out var minZResult) && minZResult >= 0 && minZResult <= 250 && minZResult != _ctrl.SearchMinZ)
		{
			_ctrl.SearchMinZ = minZResult;
			_ctrl.SaveConfig();
		}
		if (int.TryParse(sMaxZ, out var maxZResult) && maxZResult >= 0 && maxZResult <= 250 && maxZResult != _ctrl.SearchMaxZ)
		{
			_ctrl.SearchMaxZ = maxZResult;
			_ctrl.SaveConfig();
		}

		GUILayout.Space(4f);
		int coverX = _ctrl.SearchMaxX - _ctrl.SearchMinX;
		int coverZ = _ctrl.SearchMaxZ - _ctrl.SearchMinZ;
		GUILayout.Label($"自定义框大小: {coverX}×{coverZ}", _labelSubStyle);
	}

	private void DrawBasketballTab(float contentWidth)
	{
		if ((UnityEngine.Object)(object)_bball == (UnityEngine.Object)null)
		{
			GUILayout.Label("金玲功能未加载（缺少 BasketballController 组件，请检查插件入口是否已挂载）", _labelStyle);
			return;
		}

		GUILayout.Label("独立功能 · 与自动脚本互不影响，可单独开关", _labelSubStyle);
		GUILayout.Space(4f);

		// 补上「投篮」功能名称标题（与下方「拉杆机」对应，不改动任何核心功能代码）
		GUILayout.Label("投篮", _fieldLabelStyle);

		string runLabel = _bball.Enabled ? "● 运行中" : "○ 已停止";
		GUIStyle runStyle = MakeSelectableBtnStyle(_bball.Enabled);
		if (GUILayout.Button(runLabel, runStyle, GUILayout.Height(28f), GUILayout.Width(120f)))
		{
			_bball.ToggleEnabled();
		}
		GUILayout.Label("热键 Alt+B 开关（也可以直接点上面的按钮）", _labelSubStyle);

		GUILayout.Space(6f);
		DrawSeparator();
		GUILayout.Space(6f);

		GUILayout.Label($"按住时长: {_bball.HoldSeconds:0.00} 秒（默认 0.65）", _fieldLabelStyle);
		float hold = GUILayout.HorizontalSlider(_bball.HoldSeconds, BasketballController.MinHoldSeconds, BasketballController.MaxHoldSeconds);
		hold = Mathf.Round(hold * 100f) / 100f;
		if (Mathf.Abs(hold - _bball.HoldSeconds) > 0.001f)
		{
			_bball.HoldSeconds = hold;
			_bball.SaveConfig();
		}
		GUILayout.Space(4f);

		GUILayout.Label($"松开后间隔: {_bball.IntervalMs:0} 毫秒", _fieldLabelStyle);
		float interval = GUILayout.HorizontalSlider(_bball.IntervalMs, BasketballController.MinIntervalMs, BasketballController.MaxIntervalMs);
		interval = Mathf.Round(interval / 10f) * 10f;
		if (Mathf.Abs(interval - _bball.IntervalMs) > 0.01f)
		{
			_bball.IntervalMs = interval;
			_bball.SaveConfig();
		}
		GUILayout.Space(4f);

		int currentStamina = (EClass.pc != null) ? EClass.pc.stamina.value : 0;
		GUILayout.Label($"精力低于等于 {_bball.StopStaminaThreshold} 时自动停止（当前精力: {currentStamina}）", _fieldLabelStyle);
		int threshold = (int)GUILayout.HorizontalSlider((float)_bball.StopStaminaThreshold, 1f, 10f);
		if (threshold != _bball.StopStaminaThreshold)
		{
			_bball.StopStaminaThreshold = threshold;
			_bball.SaveConfig();
		}

		GUILayout.Space(6f);
		DrawSeparator();
		GUILayout.Space(6f);

		string stateText = "空闲";
		if (_bball.CurrentState == BasketballController.RunState.Holding)
		{
			stateText = "按住中";
		}
		else if (_bball.CurrentState == BasketballController.RunState.Waiting)
		{
			stateText = "间隔等待中";
		}
		GUILayout.Label("当前状态: " + stateText, _labelStyle);

		GUILayout.Space(4f);
		GUILayout.Label("⚠ 真实鼠标模拟：点击作用在光标当前位置，运行前请将光标停在目标处；切勿在运行时切到其他窗口。", _labelSubStyle);

		GUILayout.Space(6f);
		DrawSeparator();
		GUILayout.Space(6f);

		GUILayout.Label("拉杆机", _fieldLabelStyle);
		GUILayout.Label("固定按4次，间隔可自定义（毫秒），改完立即生效并保存", _labelSubStyle);
		GUILayout.Space(4f);

		GUILayout.BeginHorizontal();
		GUILayout.Label("间隔1:", _labelStyle, GUILayout.Width(44f));
		GUI.SetNextControlName("lever1Field");
		string sInt1 = GUILayout.TextField(_bball.LeverInterval1Ms.ToString(), GUILayout.Width(58f));
		GUILayout.Space(6f);
		GUILayout.Label("间隔2:", _labelStyle, GUILayout.Width(44f));
		GUI.SetNextControlName("lever2Field");
		string sInt2 = GUILayout.TextField(_bball.LeverInterval2Ms.ToString(), GUILayout.Width(58f));
		GUILayout.Space(6f);
		GUILayout.Label("间隔3:", _labelStyle, GUILayout.Width(44f));
		GUI.SetNextControlName("lever3Field");
		string sInt3 = GUILayout.TextField(_bball.LeverInterval3Ms.ToString(), GUILayout.Width(58f));
		GUILayout.EndHorizontal();

		if (int.TryParse(sInt1, out var int1Result) && int1Result >= BasketballController.MinLeverIntervalMs && int1Result <= BasketballController.MaxLeverIntervalMs && int1Result != _bball.LeverInterval1Ms)
		{
			_bball.LeverInterval1Ms = int1Result;
			_bball.SaveConfig();
		}
		if (int.TryParse(sInt2, out var int2Result) && int2Result >= BasketballController.MinLeverIntervalMs && int2Result <= BasketballController.MaxLeverIntervalMs && int2Result != _bball.LeverInterval2Ms)
		{
			_bball.LeverInterval2Ms = int2Result;
			_bball.SaveConfig();
		}
		if (int.TryParse(sInt3, out var int3Result) && int3Result >= BasketballController.MinLeverIntervalMs && int3Result <= BasketballController.MaxLeverIntervalMs && int3Result != _bball.LeverInterval3Ms)
		{
			_bball.LeverInterval3Ms = int3Result;
			_bball.SaveConfig();
		}

		GUILayout.Space(4f);
		GUILayout.Label($"当前序列：按下 → {_bball.LeverInterval1Ms}ms → 按下 → {_bball.LeverInterval2Ms}ms → 按下 → {_bball.LeverInterval3Ms}ms → 按下（共4次，不循环）", _labelSubStyle);
		GUILayout.Space(4f);

		bool leverBusy = _bball.LeverRunning;
		string leverLabel = leverBusy ? "执行中…" : "拉杆";
		GUIStyle leverStyle = MakeSelectableBtnStyle(leverBusy);
		GUI.enabled = !leverBusy;
		if (GUILayout.Button(leverLabel, leverStyle, GUILayout.Height(28f), GUILayout.Width(120f)))
		{
			_bball.TriggerLever();
		}
		GUI.enabled = true;
		GUILayout.Label("热键 Alt+A 触发", _labelSubStyle);
	}

	private Texture2D MakeTex(int width, int height, Color col)
	{
		Color[] array = new Color[width * height];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = col;
		}
		Texture2D tex = new Texture2D(width, height);
		tex.SetPixels(array);
		tex.Apply();
		return tex;
	}
}