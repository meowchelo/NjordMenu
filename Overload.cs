
using Hazel;
using Rewired.UI.ControlMapper;
using UnityEngine;

namespace NjordMenu;

public static class OverloadHandler
{
    public static float cooldown;
    public static int strength;
    private static HashSet<int> _customTargets = new();
    private static float _timer;
    private static Dictionary<int, int> _rpcCounters = new();
    private static int _nextTarget = int.MinValue;
    private static bool _hasRun;

    public static byte GetCurrentMapID()
    {
        if (GameOptionsManager.Instance?.currentGameOptions != null)
        {
            return GameOptionsManager.Instance.currentGameOptions.MapId;
        }

        return byte.MaxValue;
    }

    public static void Overload(int targetId, int strength)
    {

        bool hasLadders = ShipStatus.Instance && ((MapNames)GetCurrentMapID() == MapNames.Fungle || (MapNames)GetCurrentMapID() == MapNames.Airship);

        uint netId = hasLadders ? PlayerControl.LocalPlayer.NetId : PlayerControl.LocalPlayer.MyPhysics.NetId;
        byte rpcCall = hasLadders ? (byte)RpcCalls.SetStartCounter : (byte)RpcCalls.ClimbLadder;

        for (int i = 0; i < strength; i++) // Strength = Num of malformed RPCs sent
        {
            MessageWriter overloadMsg = AmongUsClient.Instance.StartRpcImmediately(netId, rpcCall, SendOption.None, targetId);
            AmongUsClient.Instance.FinishRpcImmediately(overloadMsg);
        }
    }

    public static void Run()
    {
        if (!NjordMenuGUI.runOverload || OverloadUI.currentTargets.Count <= 0)
        {
            _timer = cooldown;
            _nextTarget = int.MinValue;
            _hasRun = false;
            _rpcCounters.Clear();
            return;
        }

        _timer += Time.unscaledDeltaTime;

        if (_timer >= cooldown)
        {

            if (OverloadUI.maxPossibleTargets == OverloadUI.currentTargets.Count)
            {
                int broadcastId = -1;

                Overload(broadcastId, strength);
                _timer -= cooldown;

                return;
            }

            var currentTargets = OverloadUI.currentTargets;

            foreach (NetworkedPlayerInfo targetData in currentTargets)
            {
                int clientId = targetData.ClientId;

                if (!_hasRun)
                {
                    if (_nextTarget == int.MinValue || clientId == _nextTarget)
                    {
                        Overload(clientId, strength);
                        _timer -= cooldown;

                        _hasRun = true;
                    }
                }
                else
                {
                    _nextTarget = clientId;
                    _hasRun = false;

                    return;
                }
            }

            _nextTarget = int.MinValue;
            _hasRun = false;

            _rpcCounters.Clear();
        }
    }

    public static void AddCustomTarget(NetworkedPlayerInfo playerData)
    {
        int clientId = playerData.ClientId;
        _customTargets.Add(clientId);
    }

    public static void RemoveCustomTarget(NetworkedPlayerInfo playerData)
    {
        int clientId = playerData.ClientId;
        _customTargets.Remove(clientId);
    }

    public static bool IsCustomTarget(NetworkedPlayerInfo playerData)
    {
        return _customTargets.Contains(playerData.ClientId);
    }

    public static (HashSet<TargetType> targetTypes, bool isTarget) GetTarget(NetworkedPlayerInfo playerData)
    {
        bool isTarget = false;
        var targetTypes = new HashSet<TargetType>();

        if (NjordMenuGUI.overloadAll)
        {
            targetTypes.Add(TargetType.All);
            isTarget = true;
        }

        bool hostTarget = NjordMenuGUI.overloadHost && AmongUsClient.Instance.HostId == playerData.ClientId;
        if (hostTarget)
        {
            targetTypes.Add(TargetType.Host);
            isTarget = true;
        }

        if (playerData.Role != null)
        {
            RoleTeamTypes roleTeamType = playerData.Role.TeamType;

            bool crewTarget = NjordMenuGUI.overloadCrew && roleTeamType.Equals(RoleTeamTypes.Crewmate);
            if (crewTarget)
            {
                targetTypes.Add(TargetType.Crewmate);
                isTarget = true;
            }

            bool impTarget = NjordMenuGUI.overloadImps && roleTeamType.Equals(RoleTeamTypes.Impostor);
            if (impTarget)
            {
                targetTypes.Add(TargetType.Impostor);
                isTarget = true;
            }
        }

        bool customTarget = IsCustomTarget(playerData);
        if (customTarget)
        {
            targetTypes.Add(TargetType.Custom);
            isTarget = true;
        }

        if (!isTarget)
        {
            targetTypes.Add(TargetType.None);
        }

        return (targetTypes, isTarget);
    }

    public static void ClearCustomTargets()
    {
        _customTargets.Clear();
    }

    public static void PopulateCustomTargets(PlayerControl[] players, TargetType targetType)
    {
        int playerCount = players.Length;

        for (int i = 0; i < playerCount; i++)
        {
            NetworkedPlayerInfo playerData = players[i].Data;
            var playerTarget = GetTarget(playerData);
            bool isTarget = playerTarget.isTarget;

            if (isTarget && !IsCustomTarget(playerData))
            {
                HashSet<TargetType> currentTargetTypes = playerTarget.targetTypes;
                if (currentTargetTypes.Contains(targetType))
                {
                    AddCustomTarget(playerData);
                }
            }
        }
    }

    public static int GetPing()
    {
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmClient)
        {
            return AmongUsClient.Instance.Ping;
        }
        else
        {
            return 0; 
        }
    }

    public static (int strength, float cooldown) CalculateAdaptedValues()
    {
        int targetCount = OverloadUI.maxPossibleTargets == OverloadUI.currentTargets.Count
                        ? 1 
                        : Math.Max(1, OverloadUI.currentTargets.Count); 

        float maxCooldown = 2.5f;
        float cooldown = maxCooldown / targetCount;

        int pingLevel = Math.Max(1, GetPing() / 100); 

        int maxStrength = 555;
        int strength = Math.Max(1, maxStrength / pingLevel / targetCount);

        return (strength, cooldown);
    }

    public enum TargetType
    {
        None,
        All,
        Custom,
        Host,
        Impostor,
        Crewmate
    }
}

public class OverloadUI : MonoBehaviour
{
    public static int numSuccesses;
    public static int maxPossibleTargets;
    public static int killSwitchThreshold;
    public static HashSet<NetworkedPlayerInfo> currentTargets = new HashSet<NetworkedPlayerInfo>(new NetPlayerInfoCidComparer());
    private HashSet<NetworkedPlayerInfo> _tmpTargets = new HashSet<NetworkedPlayerInfo>(new NetPlayerInfoCidComparer());
    private bool _areTargetsUnlocked => !NjordMenuGUI.runOverload || !NjordMenuGUI.olLockTargets;
    private bool _hasAutoStarted;

    private Rect _windowRect = new(320, 10, 595, 250);
    private GUIStyle _targetButtonStyle;
    private GUIStyle _normalButtonStyle;
    private GUIStyle _logStyle;

    // Overload Console elements
    private static Vector2 _scrollPosition = Vector2.zero;
    private static List<string> _logEntries = new();
    private const int MaxLogEntries = 300;

    private void Start()
    {
        killSwitchThreshold = 500 * 5;

        if (!NjordMenuGUI.olAutoAdapt)
        {
            OverloadHandler.strength = 666;
            OverloadHandler.cooldown = 0.5f;
        }
    }

    private void Update()
    {
        var players = PlayerControl.AllPlayerControls.ToArray().Where(player => player?.Data != null && !player.AmOwner).ToArray();
        maxPossibleTargets = players.Length;

        if (AmongUsClient.Instance && AmongUsClient.Instance.NetworkMode != NetworkModes.FreePlay)
        {
            for (int i = 0; i < maxPossibleTargets; i++)
            {
                NetworkedPlayerInfo playerData = players[i].Data;
                var playerTarget = OverloadHandler.GetTarget(playerData);

                bool isTarget = playerTarget.isTarget;

                if (_areTargetsUnlocked)
                {
                    if (isTarget)
                    {
                        _tmpTargets.Add(playerData);
                    }
                }
                else
                {
                    if (currentTargets.Contains(playerData))
                    {
                        _tmpTargets.Add(playerData);
                    }
                }
            }
        }

        if (AmongUsClient.Instance.AmClient && AmongUsClient.Instance)
        {
            var old = currentTargets;
            currentTargets = _tmpTargets;
            _tmpTargets = old;
        }
        else
        {
            if (!NjordMenuGUI.runOverload) currentTargets.Clear();

            _hasAutoStarted = false;
        }

        _tmpTargets.Clear();

        if (NjordMenuGUI.olAutoAdapt)
        {
            var adaptedValues = OverloadHandler.CalculateAdaptedValues();

            OverloadHandler.strength = adaptedValues.strength;
            OverloadHandler.cooldown = adaptedValues.cooldown;
        }

        int numCurrentTargets = currentTargets.Count;

        if (NjordMenuGUI.runOverload)
        {
            bool doAutoStop = NjordMenuGUI.olAutoStop && numCurrentTargets <= 0;

            bool isLagging = OverloadHandler.GetPing() > killSwitchThreshold;
            bool doKillSwitch = NjordMenuGUI.olKillSwitch && isLagging;

            if (doAutoStop || doKillSwitch)
            {
                string extraStr = doKillSwitch ? " : ! Kill Switch !" : "";
                StopOverload(extraStr);
            }
        }
        else
        {
            if (PlayerControl.LocalPlayer && NjordMenuGUI.olAutoStart && !_hasAutoStarted && numCurrentTargets > 0)
            {
                _hasAutoStarted = true;
                StartOverload();
            }
        }
    }

    private void OnGUI()
    {
        if (!NjordMenuGUI.showOverload) return;

        InitStyles();


        _windowRect = GUI.Window(69, _windowRect, (GUI.WindowFunction)OverloadWindow, "Overload");
    }

    private void OverloadWindow(int windowID)
    {
        GUILayout.BeginHorizontal();

        GUILayout.Space(15f);

        GUILayout.BeginVertical();

        GUILayout.Space(5f);

        var players = PlayerControl.AllPlayerControls.ToArray().Where(player => player?.Data != null && !player.AmOwner).ToArray();
        var playerCount = players.Length;

        GUILayout.BeginHorizontal();

        if (playerCount > 0 && AmongUsClient.Instance.NetworkMode != NetworkModes.FreePlay)
        {
            GUILayout.BeginVertical(GUILayout.ExpandWidth(false));

            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));

            DrawPlayers(players, playerCount);

            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            GUILayout.Space(10f);
        }

        GUILayout.BeginVertical();

        DrawSelectionToggles();

        if (NjordMenuGUI.overloadReset)
        {
            NjordMenuGUI.overloadAll = false;
            NjordMenuGUI.overloadHost = false;
            NjordMenuGUI.overloadCrew = false;
            NjordMenuGUI.overloadImps = false;

            OverloadHandler.ClearCustomTargets();

            NjordMenuGUI.overloadReset = false;
        }

        GUILayout.EndVertical();

        GUILayout.Space(40f);

        GUILayout.EndHorizontal();

        GUILayout.Space(10f);

        GUILayout.Box("", NjordMenuGUI.safeLineStyle, GUILayout.Height(1f), GUILayout.Width(420f));

        GUILayout.Space(10f);

        GUILayout.BeginHorizontal();

        DrawStateButtons();

        GUILayout.Space(3f);

        DrawStateLabel();

        GUILayout.EndHorizontal();

        GUILayout.EndVertical();

        GUILayout.EndHorizontal();

        GUI.DragWindow();
    }

    private void InitStyles()
    {
        if (_targetButtonStyle == null)
        {
            _targetButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Italic
            };
        }

        if (_normalButtonStyle == null)
        {
            _normalButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold
            };
        }

        if (_logStyle == null)
        {
            _logStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15
            };
        }
    }

    public static void StartOverload()
    {
        NjordMenuGUI.runOverload = true;

        numSuccesses = 0;
    }

    public static void StopOverload(string extraStr = "")
    {
        NjordMenuGUI.runOverload = false;

        numSuccesses = 0;
    }

    private void DrawPlayers(PlayerControl[] players, int playerCount)
    {
        for (int i = 0; i < playerCount; i++)
        {
            int num = i + 1;

            NetworkedPlayerInfo playerData = players[i].Data;

            var playerTarget = OverloadHandler.GetTarget(playerData);
            bool isTarget = playerTarget.isTarget;

            Color playerBackgroundColor = playerData.Color;
            Color playerContentColor = Color.Lerp(playerBackgroundColor, Color.white, 0.5f);

            Color standardBackgroundColor = GUI.backgroundColor;
            Color standardContentColor = GUI.contentColor;

            GUI.backgroundColor = isTarget ? Color.black : playerBackgroundColor;
            GUI.contentColor = playerContentColor;

            GUIStyle style = isTarget ? _targetButtonStyle : _normalButtonStyle;

            bool isPressed = GUILayout.Button(playerData.DefaultOutfit.PlayerName, style, GUILayout.Width(140f));

            if (isPressed && _areTargetsUnlocked)
            {
                if (isTarget)
                {

                    HashSet<OverloadHandler.TargetType> targetTypes = playerTarget.targetTypes;

                    if (targetTypes.Contains(OverloadHandler.TargetType.All))
                    {
                        OverloadHandler.PopulateCustomTargets(players, OverloadHandler.TargetType.All);
                        NjordMenuGUI.overloadAll = false;
                    }

                    if (targetTypes.Contains(OverloadHandler.TargetType.Host))
                    {
                        OverloadHandler.PopulateCustomTargets(players, OverloadHandler.TargetType.Host);
                        NjordMenuGUI.overloadHost = false;
                    }

                    if (targetTypes.Contains(OverloadHandler.TargetType.Crewmate))
                    {
                        OverloadHandler.PopulateCustomTargets(players, OverloadHandler.TargetType.Crewmate);
                        NjordMenuGUI.overloadCrew = false;
                    }
                    else if (targetTypes.Contains(OverloadHandler.TargetType.Impostor))
                    {
                        OverloadHandler.PopulateCustomTargets(players, OverloadHandler.TargetType.Impostor);
                        NjordMenuGUI.overloadImps = false;
                    }

                    OverloadHandler.RemoveCustomTarget(playerData);
                }
                else
                {
                    OverloadHandler.AddCustomTarget(playerData);
                }
            }

            // Reset UI color
            GUI.backgroundColor = standardBackgroundColor;
            GUI.contentColor = standardContentColor;

            // UI shows rows of 3 buttons (1 button per player)
            if (num % 3 == 0 && num < playerCount)
            {
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));
            }
        }
    }

    private void DrawSelectionToggles()
    {
        bool newOverloadAll = GUILayout.Toggle(NjordMenuGUI.overloadAll, " All");
        NjordMenuGUI.overloadAll = _areTargetsUnlocked ? newOverloadAll : false;

        bool newOverloadHost = GUILayout.Toggle(NjordMenuGUI.overloadHost, " Host");
        NjordMenuGUI.overloadHost = _areTargetsUnlocked ? newOverloadHost : false;

        bool newOverloadCrew = GUILayout.Toggle(NjordMenuGUI.overloadCrew, " Crewmates");
        NjordMenuGUI.overloadCrew = _areTargetsUnlocked ? newOverloadCrew : false;

        bool newOverloadImps = GUILayout.Toggle(NjordMenuGUI.overloadImps, " Impostors");
        NjordMenuGUI.overloadImps = _areTargetsUnlocked ? newOverloadImps : false;

        bool newOverloadReset = GUILayout.Toggle(NjordMenuGUI.overloadReset, " Reset");
        NjordMenuGUI.overloadReset = _areTargetsUnlocked ? newOverloadReset : false;
    }

    private void DrawStateButtons()
    {
        Color standardBackgroundColor = GUI.backgroundColor;

        bool startEnabled = !NjordMenuGUI.runOverload && PlayerControl.LocalPlayer;

        Color startBackgroundColor = Color.green;
        GUI.backgroundColor = startEnabled ? startBackgroundColor : Color.black;

        if (GUILayout.Button("START", GUILayout.Width(140f)) && startEnabled)
        {
            StartOverload();
        }

        // Reset UI color
        GUI.backgroundColor = standardBackgroundColor;

        // isPlayer check is unnecessary as MenuUI check already enforces it for runOverload
        bool stopEnabled = NjordMenuGUI.runOverload;

        Color stopBackgroundColor = Color.red;
        GUI.backgroundColor = stopEnabled ? stopBackgroundColor : Color.black;

        if (GUILayout.Button("STOP", GUILayout.Width(140f)) && stopEnabled)
        {
            StopOverload();
        }

        // Reset UI color
        GUI.backgroundColor = standardBackgroundColor;
    }

    private void DrawStateLabel()
    {
        if (NjordMenuGUI.runOverload)
        {
            Color onColor = Color.Lerp(Palette.AcceptedGreen, Color.white, 0.5f);
            string colorStr = ColorUtility.ToHtmlStringRGB(onColor);

            string firstStr = $"<b><color=#{colorStr}> On : ";
            string middleStr;
            string finalStr = "</color></b>";

            if (currentTargets.Count > 0)
            {
                string pluralStr = currentTargets.Count != 1 ? "s" : "";
                middleStr = $"Attacking {currentTargets.Count} target{pluralStr}";
            }
            else
            {
                middleStr = "Idle";
            }

            GUILayout.Label($"{firstStr}{middleStr}{finalStr}");
        }
        else
        {
            Color offColor = Color.Lerp(Palette.DisabledGrey, Color.white, 0.6f);
            string colorStr = ColorUtility.ToHtmlStringRGB(offColor);

            string middleStr = "";
            if (currentTargets.Count > 0)
            {
                string pluralStr = currentTargets.Count != 1 ? "s" : "";
                middleStr = $" : {currentTargets.Count} target{pluralStr} selected";
            }

            GUILayout.Label($"<b><color=#{colorStr}> Off{middleStr}</color></b>");
        }
    }
}

public sealed class NetPlayerInfoCidComparer : IEqualityComparer<NetworkedPlayerInfo>
{
    public bool Equals(NetworkedPlayerInfo data1, NetworkedPlayerInfo data2)
    {
        return data1.ClientId == data2.ClientId;
    }

    public int GetHashCode(NetworkedPlayerInfo data)
    {
        return data.ClientId;
    }
}
