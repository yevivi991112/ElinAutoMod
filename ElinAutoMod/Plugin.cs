using BepInEx;
using UnityEngine;

namespace ElinAutoMod;

[BepInPlugin("com.mymod.elinauto", "Elin Auto Gather", "1.0.0")]
public class Plugin : BaseUnityPlugin
{
	private GameObject _go;

	private void Awake()
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Expected O, but got Unknown
		_go = new GameObject("ElinAutoMod");
		_go.AddComponent<AutoController>();
		_go.AddComponent<ModUI>();
		_go.AddComponent<BasketballController>();
		Object.DontDestroyOnLoad((Object)(object)_go);
		Logger.LogInfo((object)"ElinAutoMod loaded!");
	}
}
