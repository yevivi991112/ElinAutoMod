using UnityEngine;

namespace ElinAutoMod;

public static class ToolSwitcher
{
	public static void SwitchSlot(int slotIndex)
	{
		WidgetCurrentTool instance = WidgetCurrentTool.Instance;
		if ((Object)(object)instance == (Object)null || slotIndex < 0 || slotIndex >= 10)
		{
			return;
		}
		if (WidgetCurrentTool.dirty)
		{
			instance.CheckDirty();
		}
		if (instance.list == null || instance.list.buttons == null || slotIndex >= instance.list.buttons.Count || slotIndex >= instance.maxSlots)
		{
			return;
		}
		// 绝不强制切换玩家当前所在的快捷栏页 (instance.page)，尊重玩家将工具放在第1页或第2页的习惯。
		// WidgetCurrentTool.Select 在 selected == slotIndex && selectedPage == page 时会把 index 置为 -1（反选变空手）。
		// 因此若当前已选中该槽位且手持工具与槽位卡片一致，直接返回；若手持为空或处于技能状态，则先置 selected = -1 再调用 Select。
		if (instance.selected == slotIndex && instance.selectedPage == instance.page)
		{
			ButtonGrid curBtn = instance.selectedButton;
			Card expectedCard = ((Object)(object)curBtn != (Object)null) ? curBtn.card : null;
			if (EClass.pc != null && ((Card)EClass.pc).Tool != null && expectedCard != null && ((Card)EClass.pc).Tool == expectedCard && !(EClass.player?.currentHotItem is HotItemAct))
			{
				return;
			}
			instance.selected = -1;
		}
		// 传 fromHotkey = false，防止触发 WidgetCurrentTool.Select 中的 useAbilityOnHotkey 释放技能或音效刷屏
		instance.Select(slotIndex, false);
	}

	/// <summary>检查指定快捷栏槽位当前是否已真实选中且玩家手中持有的正是该槽位中的工具。</summary>
	public static bool IsSlotActuallyEquipped(int slotIndex)
	{
		WidgetCurrentTool instance = WidgetCurrentTool.Instance;
		if ((Object)(object)instance == (Object)null || slotIndex < 0 || slotIndex >= 10 || EClass.pc == null)
		{
			return false;
		}
		if (WidgetCurrentTool.dirty)
		{
			instance.CheckDirty();
		}
		if (instance.list == null || instance.list.buttons == null || slotIndex >= instance.list.buttons.Count || slotIndex >= instance.maxSlots)
		{
			return false;
		}
		if (instance.selected != slotIndex || instance.selectedPage != instance.page)
		{
			return false;
		}
		if (EClass.player?.currentHotItem is HotItemAct)
		{
			return false;
		}
		Thing tool = ((Card)EClass.pc).Tool;
		if (tool == null)
		{
			return false;
		}
		ButtonGrid btn = instance.selectedButton;
		return (Object)(object)btn != (Object)null && btn.card == tool;
	}

	/// <summary>读取当前快捷栏页指定槽位上的物品（若为空或越界则返回 null）。</summary>
	public static Thing GetSlotThing(int slotIndex)
	{
		WidgetCurrentTool instance = WidgetCurrentTool.Instance;
		if ((Object)(object)instance == (Object)null || slotIndex < 0 || slotIndex >= 10)
		{
			return null;
		}
		if (WidgetCurrentTool.dirty)
		{
			instance.CheckDirty();
		}
		if (instance.list == null || instance.list.buttons == null || slotIndex >= instance.list.buttons.Count || slotIndex >= instance.maxSlots)
		{
			return null;
		}
		ButtonGrid btn = instance.list.buttons[slotIndex].component as ButtonGrid;
		if ((Object)(object)btn != (Object)null && btn.card != null)
		{
			return btn.card.Thing;
		}
		int gridIdx = slotIndex + instance.page * 10;
		if (instance.grid != null && gridIdx >= 0 && gridIdx < instance.grid.Count)
		{
			return instance.grid[gridIdx];
		}
		return null;
	}

	/// <summary>切回空手（取消当前快捷栏选中项，避免手持镰刀/刷子/乐器/技能时干扰普通徒手采集）。</summary>
	public static void ClearCurrentToolSelection()
	{
		WidgetCurrentTool instance = WidgetCurrentTool.Instance;
		if ((Object)(object)instance == (Object)null)
		{
			return;
		}
		if (WidgetCurrentTool.dirty)
		{
			instance.CheckDirty();
		}
		if (instance.selected != -1 || EClass.player?.currentHotItem is HotItemAct)
		{
			instance.Select(-1, false);
		}
	}
}