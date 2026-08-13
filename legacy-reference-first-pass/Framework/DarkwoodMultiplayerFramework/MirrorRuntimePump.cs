using System;
using System.Reflection;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

internal sealed class MirrorRuntimePump : MonoBehaviour
{
	private static readonly MethodInfo ServerEarly = AccessTools.Method(typeof(NetworkServer), "NetworkEarlyUpdate", (Type[])null, (Type[])null);

	private static readonly MethodInfo ServerLate = AccessTools.Method(typeof(NetworkServer), "NetworkLateUpdate", (Type[])null, (Type[])null);

	private static readonly MethodInfo ClientEarly = AccessTools.Method(typeof(NetworkClient), "NetworkEarlyUpdate", (Type[])null, (Type[])null);

	private static readonly MethodInfo ClientLate = AccessTools.Method(typeof(NetworkClient), "NetworkLateUpdate", (Type[])null, (Type[])null);

	private void Awake()
	{
		if (ServerEarly == null || ServerLate == null || ClientEarly == null || ClientLate == null)
		{
			throw new MissingMethodException("Mirror network update methods were not found.");
		}
		Plugin.Log.LogInfo((object)"Mirror runtime update pump installed.");
	}

	private void Update()
	{
		try
		{
			if (NetworkServer.active)
			{
				ServerEarly.Invoke(null, null);
			}
			if (NetworkClient.active)
			{
				ClientEarly.Invoke(null, null);
			}
		}
		catch (Exception e)
		{
			Plugin.Log.LogError((object)("Mirror early update failed: " + Unwrap(e)));
		}
	}

	private void LateUpdate()
	{
		try
		{
			if (NetworkServer.active)
			{
				ServerLate.Invoke(null, null);
			}
			if (NetworkClient.active)
			{
				ClientLate.Invoke(null, null);
			}
		}
		catch (Exception e)
		{
			Plugin.Log.LogError((object)("Mirror late update failed: " + Unwrap(e)));
		}
	}

	private static Exception Unwrap(Exception e)
	{
		if (!(e is TargetInvocationException) || e.InnerException == null)
		{
			return e;
		}
		return e.InnerException;
	}
}
