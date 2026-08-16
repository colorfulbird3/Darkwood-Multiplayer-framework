using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

public readonly struct SceneChangeMessage
{
    public SceneChangeMessage(string scene){Scene=scene;}
    public string Scene {get;}
}

/// <summary>Generic world-interaction payload. ValueA semantics depend on the action kind.</summary>
