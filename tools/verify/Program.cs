// Offline check that every member StbcRevival patches or reflects on exists in a given
// game install, with the same lookups the plugin does at runtime (no Unity needed).
// Usage: dotnet run -- "<game dir>"
using System.Reflection;

var managed = Path.Combine(args.Length > 0 ? args[0] : Path.Combine(Environment.GetEnvironmentVariable("HOME")!,
    ".local/share/Steam/steamapps/common/Star Trek Bridge Crew"), "stbc_Data", "Managed");
var resolver = new PathAssemblyResolver(Directory.GetFiles(managed, "*.dll"));
using var mlc = new MetadataLoadContext(resolver, "mscorlib");
var game = mlc.LoadFromAssemblyPath(Path.Combine(managed, "Assembly-CSharp.dll"));
var types = game.GetTypes();
const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;
const BindingFlags AnyS = PS | BindingFlags.NonPublic;
int failures = 0;

void Check(string what, object? found, string? note = null)
{
    Console.WriteLine($"{(found != null ? "OK  " : "FAIL")} {what}{(note != null ? "  " + note : "")}");
    if (found == null) failures++;
}
string IL(MethodInfo? m) => m == null ? "" : $"(IL {m.GetMethodBody()?.GetILAsByteArray()?.Length} bytes)";

var configFile = game.GetType("SVR.ConfigFile");
var getValueString = configFile?.GetMethod("GetValueString", PS);
Check("SVR.ConfigFile.GetValueString(string, out string)",
    getValueString?.GetParameters() is { Length: 2 } p && p[1].ParameterType.IsByRef ? getValueString : null, IL(getValueString));
var containsKey = configFile?.GetMethod("ContainsKey", PS);
Check("SVR.ConfigFile.ContainsKey(string)", containsKey, IL(containsKey));

var getLoginMethod = game.GetType("SVR.Network.NetworkCore")?.GetMethod("GetLoginMethod", PS);
Check("SVR.Network.NetworkCore.GetLoginMethod()", getLoginMethod, IL(getLoginMethod));
var loginEnum = game.GetType("SVR.Network.DHCMJKJFCMJ");
Check("login enum SVR.Network.DHCMJKJFCMJ is return type, None == 0",
    loginEnum != null && getLoginMethod?.ReturnType == loginEnum && Convert.ToInt32(loginEnum.GetField("None")!.GetRawConstantValue()) == 0 ? loginEnum : null);
Check("SVR.Network.Uplay.s_skipQuit", game.GetType("SVR.Network.Uplay")?.GetField("s_skipQuit", AnyS));

var serverSettings = game.GetType("ServerSettings");
var photon = types.FirstOrDefault(t =>
    t.GetField("PhotonServerSettings", PS)?.FieldType == serverSettings && t.GetField("versionPUN", PS) != null);
Check("PhotonNetwork located structurally", photon, photon?.FullName);
Check("  ConnectUsingSettings(string)", photon?.GetMethod("ConnectUsingSettings", PS, null, new[] { mlc.CoreAssembly!.GetType("System.String")! }, null));
Check("  ConnectToRegion(region, string)", photon?.GetMethods(PS).FirstOrDefault(m => m.Name == "ConnectToRegion" && m.GetParameters().Length == 2));
// Property definitions are renamed by the obfuscator; accessor methods are not.
var getAuth = photon?.GetMethod("get_AuthValues", PS);
Check("  get_AuthValues()", getAuth, getAuth?.ReturnType.FullName);
var setAuthType = getAuth?.ReturnType.GetMethod("set_AuthType");
Check("  AuthValues.set_AuthType", setAuthType);
Check("  AuthType enum has None", setAuthType?.GetParameters()[0].ParameterType.GetField("None"));
Check("  AuthValues.set_AuthGetParameters", getAuth?.ReturnType.GetMethod("set_AuthGetParameters"));
var networkingPeer = photon?.GetFields(PS | BindingFlags.NonPublic).Select(f => f.FieldType)
    .FirstOrDefault(t => t.GetMethod("ReconnectToMaster", BindingFlags.Public | BindingFlags.Instance) != null);
Check("  NetworkingPeer located via PhotonNetwork field", networkingPeer, networkingPeer?.FullName);
var peerConnect = networkingPeer?.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
{
    var p = m.GetParameters();
    return m.Name == "Connect" && p.Length == 2 && p[0].ParameterType.FullName == "System.String"
           && p[1].ParameterType.IsEnum && p[1].ParameterType.GetField("GameServer") != null;
});
Check("  NetworkingPeer.Connect(string, ServerConnection)", peerConnect, IL(peerConnect));
const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
Check("  PhotonNetwork.SwitchToProtocol", photon?.GetMethod("SwitchToProtocol", PS));
Check("  PhotonNetwork peer field (by type)", photon?.GetFields(PS | BindingFlags.NonPublic).FirstOrDefault(f => f.FieldType == networkingPeer));
Check("  NetworkingPeer.mActors", networkingPeer?.GetField("mActors", Inst));
Check("  NetworkingPeer Dictionary<int, PhotonView> field", networkingPeer?.GetFields(Inst).FirstOrDefault(f =>
    f.FieldType.IsGenericType && f.FieldType.GetGenericArguments() is { Length: 2 } a && a[0].FullName == "System.Int32" && a[1].Name == "PhotonView"));
Check("  NetworkingPeer.get_mLocalActor", networkingPeer?.GetMethod("get_mLocalActor", Inst));
Check("  NetworkingPeer.ChangeLocalID(int)", networkingPeer?.GetMethod("ChangeLocalID", Inst));
Check("  PhotonPlayer.get_ID", networkingPeer?.GetMethod("get_mLocalActor", Inst)?.ReturnType.GetMethod("get_ID", Inst));
Check("  NetworkingPeer.OnStatusChanged / OnOperationResponse",
    networkingPeer?.GetMethod("OnStatusChanged", Inst) is not null && networkingPeer.GetMethod("OnOperationResponse", Inst) is not null ? networkingPeer : null);
// Room browser
var getRoomList = photon?.GetMethod("GetRoomList", PS);
Check("  PhotonNetwork.GetRoomList()", getRoomList, getRoomList?.ReturnType.FullName);
var roomInfo = getRoomList?.ReturnType.GetElementType();
foreach (var g in new[] { "get_name", "get_playerCount", "get_maxPlayers", "get_open", "get_customProperties" })
    Check($"  RoomInfo.{g}", roomInfo?.GetMethod(g, Inst));
Check("  PhotonNetwork.get_room / get_insideLobby",
    photon?.GetMethod("get_room", PS) is not null && photon.GetMethod("get_insideLobby", PS) is not null ? photon : null);
Check("  Room.get_name (via RoomInfo base)", photon?.GetMethod("get_room", PS)?.ReturnType.GetMethod("get_name", Inst));
Check("  Room.get_visible", photon?.GetMethod("get_room", PS)?.ReturnType.GetMethod("get_visible", Inst));
var friendsList = game.GetType("FriendsListUI");
Check("FriendsListUI.IMEKMHIKHIC (panel refresh)", friendsList?.GetMethod("IMEKMHIKHIC", Inst));
foreach (var f in new[] { "LMHDDPCJIKO", "_titleTextAegis", "_titleTextEnterprise" })
    Check($"  FriendsListUI.{f}", friendsList?.GetField(f, Inst));
Check("  FriendsListUI.Start / OnEnable", friendsList?.GetMethod("Start", Inst) is not null && friendsList.GetMethod("OnEnable", Inst) is not null ? friendsList : null);
var friend = game.GetType("SVR.Network.HPJNFNEKGNJ");
foreach (var f in new[] { "ANEGDFCKGEC", "HKMMKFJNJBJ", "<JNPNEMLBCHA>k__BackingField" })
    Check($"  friend entry field {f}", friend?.GetField(f, Inst));
foreach (var m in new[] { "ReevaluateMyLife", "UpdateUplayPCPresence", "get_CanBeInvited" })
    Check($"  friend entry {m}", friend?.GetMethod(m, Inst));
var detail = game.GetType("FriendDetailUI");
Check("FriendDetailUI join/invite handlers + IEEKEFCLGND",
    detail?.GetMethod("NGCPCFNFFNI", Inst) is not null && detail.GetMethod("JEJJLGIKFHF", Inst) is not null && detail.GetField("IEEKEFCLGND", Inst) is not null ? detail : null);
var avatarLookup = game.GetType("SVR.Network.AvatarManager")?.GetMethod("CKGEHAPOPEN", AnyS);
Check("AvatarManager.CKGEHAPOPEN(friend, out bool)", avatarLookup?.GetParameters().Select(p => p.Name).SequenceEqual(new[] { "NBHDALBLHHF", "BOJMJANONMK" }) == true ? avatarLookup : null);
Check("NetworkCore.get_LoginMethod", game.GetType("SVR.Network.NetworkCore")?.GetMethod("get_LoginMethod", Inst));
foreach (var f in new[] { "AppID", "HostType", "ServerAddress", "ServerPort", "Protocol" })
    Check($"  ServerSettings.{f}", serverSettings?.GetField(f));

var steamManager = game.GetType("SteamManager");
Check("SteamManager.get_Initialized() (static)", steamManager?.GetMethod("get_Initialized", PS));
var persona = types.Where(t => t.Namespace == "Steamworks")
    .Select(t => t.GetMethod("GetPersonaName", PS, null, Type.EmptyTypes, null))
    .FirstOrDefault(m => m != null && m.ReturnType.FullName == "System.String");
Check("Steamworks *.GetPersonaName()", persona, persona?.DeclaringType?.FullName);

// Steam mode: Photon's own Diffie-Hellman provider (PhotonCrypto) and socket internals (VirtualSockets).
var photonLib = mlc.LoadFromAssemblyPath(Path.Combine(managed, "Photon3Unity3D.dll"));
var dh = photonLib.GetType("Photon.SocketServer.Security.DiffieHellmanCryptoProvider");
Check("Photon DiffieHellmanCryptoProvider", dh);
Check("  .ctor()", dh?.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null));
Check("  PublicKey", dh?.GetProperty("PublicKey", Inst));
Check("  DeriveSharedKey(byte[])", dh?.GetMethod("DeriveSharedKey", Inst));
Check("  Decrypt(byte[], int, int)", dh?.GetMethods(Inst).FirstOrDefault(m => m.Name == "Decrypt" && m.GetParameters().Length == 3));
var socketTcp = photonLib.GetType("ExitGames.Client.Photon.SocketTcp");
Check("Photon SocketTcp Connect/Send/Disconnect",
    socketTcp?.GetMethod("Connect", Inst, null, Type.EmptyTypes, null) is not null && socketTcp.GetMethod("Send", Inst) is not null && socketTcp.GetMethod("Disconnect", Inst) is not null ? socketTcp : null);
var iSocket = photonLib.GetType("ExitGames.Client.Photon.IPhotonSocket");
Check("  IPhotonSocket.peerBase / State setter / HandleException",
    iSocket?.GetField("peerBase", Inst) is not null && iSocket.GetProperty("State", Inst)?.GetSetMethod(true) is not null && iSocket.GetMethod("HandleException", Inst) is not null ? iSocket : null);
var peerBase = photonLib.GetType("ExitGames.Client.Photon.PeerBase");
Check("  PeerBase.ServerAddress / OnConnect / peerConnectionState",
    peerBase?.GetProperty("ServerAddress", Inst) is not null && peerBase.GetMethod("OnConnect", Inst) is not null && peerBase.GetField("peerConnectionState", Inst) is not null ? peerBase : null);

var introSequence = game.GetType("IntroSequence");
Check("IntroSequence.GBDBONKGDOM() (next element) + _elements/_curNdx/_skipIsOn",
    introSequence?.GetMethod("GBDBONKGDOM", Inst) is { } nextElement && nextElement.ReturnType.FullName == "System.Int32"
    && introSequence.GetField("_elements", Inst) is not null && introSequence.GetField("_curNdx", Inst) is not null
    && introSequence.GetField("_skipIsOn", Inst) is not null ? introSequence : null);
Check("MovieIntroElement", game.GetType("MovieIntroElement"));

var pauseMenu = game.GetType("UI.PauseMenuUI");
Check("PauseMenuUI.NLDKOENAFNJ (pane switch) + _pauseMenuGlobal",
    pauseMenu?.GetMethod("NLDKOENAFNJ", Inst) is not null && pauseMenu.GetField("_pauseMenuGlobal", Inst) is not null ? pauseMenu : null);
var tmpText = game.GetType("TMPro.TMP_Text");
Check("TMP_Text.SetLocalizedText(string) + key field EEHDAODMMFJ",
    tmpText?.GetMethod("SetLocalizedText", new[] { mlc.CoreAssembly!.GetType("System.String")! }) is not null
    && tmpText.GetField("EEHDAODMMFJ", Inst) is not null ? tmpText : null);

Check("ShuttleDriver", game.GetType("ShuttleDriver"));
var pathAnimator = game.GetType("CameraPathAnimator");
Check("CameraPathAnimator.animationObject", pathAnimator?.GetField("animationObject"));

var prefs = game.GetType("SVR.Network.IOGFEJEOLBA");
Check("Preferences entity: .ctor, Set(string, object), FDJOFPBNIFF table",
    prefs?.GetConstructor(Type.EmptyTypes) is not null && prefs.GetMethod("Set") is not null && prefs.GetField("FDJOFPBNIFF", Inst) is not null ? prefs : null);
Check("OnlineSave.get_Preferences returns it", game.GetType("SVR.Network.OnlineSave")?.GetMethod("get_Preferences")?.ReturnType == prefs ? prefs : null);
var singleton = game.GetType("SVR.Singleton_Behaviour_Persistent`1");
Check("Singleton_Behaviour_Persistent.JKOEGLOEGKD (instance field)", singleton?.GetField("JKOEGLOEGKD", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static));
var json = game.GetType("SVR.Network.Utilities.MHCFEEJDKJM");
Check("JSON helper JsonToString / StringToJson", json?.GetMethod("JsonToString") is not null && json.GetMethod("StringToJson") is not null ? json : null);

var entityBase = game.GetType("SVR.Network.MFKKMDCIEJA");
Check("Save entity OANAMJIDBOO(Hashtable) (online search result)",
    entityBase?.GetMethod("OANAMJIDBOO", Inst) is { } searchResult && searchResult.GetParameters() is { Length: 1 } sp && sp[0].Name == "MIACDBOANPI" ? entityBase : null);

var news = game.GetType("NewsPage");
foreach (var m in new[] { "OnEnable", "FLBDMBHOBJG", "MLMAOLKJDHN", "OnTopNewsSelected", "OnBottomNewsSelected" })
    Check($"NewsPage.{m}", news?.GetMethod(m, Inst));
foreach (var f in new[] { "BBPGHDDIBPO", "KLCKHJPAMFD", "TopNewsSlot", "BottomNewsSlot", "TopSlotTitle", "BottomSlotTitle", "TopSlotBanner", "BottomSlotBanner",
             "DefaultBanner", "NewsListPage", "NoNewsImage", "DetailsPage", "PrevPageButton", "NextPageButton" })
    Check($"  NewsPage.{f}", news?.GetField(f, Inst));
var invites = game.GetType("InviteActionUI");
Check("InviteActionUI.BAPLBKELENL / SetDirty", invites?.GetMethod("BAPLBKELENL", Inst) is not null && invites.GetMethod("SetDirty", Inst) is not null ? invites : null);
foreach (var f in new[] { "KJEOKFJEMIH", "_entryObjects", "PPJBBELBAFH", "KLCKHJPAMFD", "DGJOHLEKBNJ", "JNELKOHEPPF", "OACCJDCBMDP", "_upButtonInteraction", "_downButtonInteraction" })
    Check($"  InviteActionUI.{f}", invites?.GetField(f, Inst));
var inviteEntry = game.GetType("InviteActionEntry");
foreach (var f in new[] { "_nameText", "_actionText", "_avatarIcon" })
    Check($"  InviteActionEntry.{f}", inviteEntry?.GetField(f, Inst));
Check("  InviteActionEntry.OnClicked", inviteEntry?.GetMethod("OnClicked", Inst));
var friendAction = game.GetType("FriendActionUI");
Check("FriendActionUI.BAPLBKELENL", friendAction?.GetMethod("BAPLBKELENL", Inst));
foreach (var f in new[] { "_entryObjects", "_pageCountObject", "_upButtonInteraction", "_downButtonInteraction", "GOLGOCFPNGB" })
    Check($"  FriendActionUI.{f}", friendAction?.GetField(f, Inst));
Check("TMP_Text.get_text", tmpText?.GetMethod("get_text", Inst));
Check("PopupMgr.ShowPopup(string, LGNBEBILNCL, PDHCCBFJODN, bool, string[])", game.GetType("UI.PopupMgr")?.GetMethods(PS).FirstOrDefault(m => m.Name == "ShowPopup" && m.GetParameters().Length == 5));

Check("WwiseSoundUtil.PostEvent(string, GameObject)", game.GetType("WwiseSoundUtil")?.GetMethods(PS).FirstOrDefault(m =>
    m.Name == "PostEvent" && m.GetParameters() is { Length: 2 } pp && pp[0].ParameterType.FullName == "System.String"));
Check("MicCheckUI reads skipMicCheck", game.GetType("MicCheckUI")?.GetMethod("DNNAEKAMFLG", Inst));

// Revival missions
var missionManager = game.GetType("Mission.MissionManager");
Check("MissionManager.GenerateProceduralMission + ENGIPFEKJAK (script runner)",
    missionManager?.GetMethod("GenerateProceduralMission") is not null && missionManager.GetField("ENGIPFEKJAK", Inst) is { } runnerField
    && runnerField.FieldType.GetConstructors(Inst).Any(c => c.GetParameters().Length == 2) ? missionManager : null);
foreach (var t in new[] { "Mission.Scripting.MNFBGALLEAJ", "Mission.Scripting.MJHDDKANLEC", "Mission.Scripting.KOOLIJPNDOH", "AI.CPLIILIDJKG" })
    Check($"  mission framework type {t}", game.GetType(t));
var briefing = game.GetType("ConsoleBriefingRoom");
Check("ConsoleBriefingRoom.Start / BatchUpdate", briefing?.GetMethod("Start", Inst) is not null && briefing.GetMethod("BatchUpdate", Inst) is not null ? briefing : null);
foreach (var f in new[] { "_proceduralMissionToggle", "_campaignMissionToggle", "_aegisToggle", "_enterpriseToggle", "_tngToggle", "_nextMissionButton",
             "_previousMissionButton", "_experienceWarningText", "_missionName", "_missionDescriptionText", "_enabledOptionsColor", "_disabledOptionsColor",
             "_aegisToggleText", "_enterpriseToggleText", "_tngToggleText", "_enabledOptionsColorTNG", "_disabledOptionsColorTNG" })
    Check($"  ConsoleBriefingRoom.{f}", briefing?.GetField(f, Inst));
Check("ConsoleBriefingRoom.NKIGJPBIGND / JEKNGMEIFGM (next/previous mission)", briefing?.GetMethod("NKIGJPBIGND", Inst) is not null
    && briefing.GetMethod("JEKNGMEIFGM", Inst) is not null ? briefing : null);
Check("Room.SetCustomProperties", photon?.GetMethod("get_room", PS)?.ReturnType.GetMethod("SetCustomProperties", Inst));
Check("KHDFGIINMKJ.SetInteractable(Selectable, bool)", game.GetType("KHDFGIINMKJ")?.GetMethods(PS).FirstOrDefault(m =>
    m.Name == "SetInteractable" && m.GetParameters() is { Length: 2 } sip && sip[1].ParameterType.FullName == "System.Boolean"));
var helm = game.GetType("ConsoleHelm");
foreach (var m in new[] { "get_CanWarp", "get_CanImpulse", "OnEngageWarpDown", "OnEngageImpulseDown", "OnEngageWarpImpulse", "OnWarpThrottleValueChanged" })
    Check($"ConsoleHelm.{m} (Last Stand travel lock)", helm?.GetMethod(m, Inst));
var shipModifier = game.GetType("Mission.Scripting.Framework.Tasks.KAGMFNHKOPC");
foreach (var m in new[] { "WithTargets", "SetHullHealthPercent", "SetShieldHealthPercent", "SetSubsystemHealthPercent", "SetTorpedoCount" })
    Check($"  ship modifier task {m} (Last Stand repair)", shipModifier?.GetMethods(Inst).FirstOrDefault(x => x.Name == m));
Check("LauncherGroup._startingAmmo", game.GetType("LauncherGroup")?.GetField("_startingAmmo", Inst));
Check("  LauncherGroup.set_RemainingAmmo", game.GetType("LauncherGroup")?.GetMethod("set_RemainingAmmo", Inst));
foreach (var f in new[] { "_foreLaunchers", "_aftLaunchers" })
    Check($"  BaseSpacecraft.{f}", game.GetType("Sector.Spacecraft.BaseSpacecraft")?.GetField(f, Inst));
var damage = game.GetType("DamageManager");
Check("DamageManager.MKKPCHBHOJK / AHOLJBPEIDO / KEEIKMDLMJO (bridge damage)", damage?.GetMethod("MKKPCHBHOJK", Inst) is not null
    && damage.GetProperty("AHOLJBPEIDO", Inst) is not null && damage.GetProperty("KEEIKMDLMJO", Inst) is not null ? damage : null);
foreach (var f in new[] { "_persistentDamageEffectObjects", "_damageThresholds", "HIHNENIJNKE" })
    Check($"  DamageManager.{f}", damage?.GetField(f, Inst));
Check("PersistentBridgeDamageEffect._meshesToDisable / Stop", game.GetType("PersistentBridgeDamageEffect") is { } pbde
    && pbde.GetField("_meshesToDisable", Inst) is not null && pbde.GetMethod("Stop", Inst) is not null ? pbde : null);

Check("NPCHailingView._characterPrefab (avatar dump)", game.GetType("NPCHailingView")?.GetField("_characterPrefab", Inst));
Check("  NPCHailingManager._hailingViews", game.GetType("NPCHailingManager")?.GetField("_hailingViews", Inst));

var faceCustomization = game.GetType("FaceCustomization");
foreach (var f in new[] { "LookMaterialNames", "_originalLookIdx" })
    Check($"FaceCustomization.{f} (Romulan face)", faceCustomization?.GetField(f, Inst));
Check("  FaceCustomization.SetDefaultLook", faceCustomization?.GetMethod("SetDefaultLook", Inst));
Check("  FaceCustomization.DNNOHHKPGDA / CAJFLMIJGLL (face material swaps)", faceCustomization?.GetMethod("DNNOHHKPGDA", Inst) is not null
    && faceCustomization.GetMethod("CAJFLMIJGLL", Inst) is not null ? faceCustomization : null);
Check("  CustomizationManager.FFJOIFLJFPF (creator avatars)", game.GetType("CustomizationManager")?.GetField("FFJOIFLJFPF", Inst));
Check("  NovaPlayerManager.get_Instance + PlayerManager.CharacterProfiles", game.GetType("NovaPlayerManager")?.GetMethod("get_Instance", PS) is not null
    && game.GetType("SVR.PlayerManager")?.GetField("CharacterProfiles", Inst) is { } cpf ? cpf : null);
var creatorType = game.GetType("CustomizationManager");
foreach (var m in new[] { "OGEINMNDPMI", "KPAOOOCNEKP", "PHHOOCMCLIJ" })
    Check($"  CustomizationManager.{m} (ROMULAN choice)", creatorType?.GetMethod(m, Inst));
foreach (var f in new[] { "EPNEDLICCMJ", "COABBGKIHFL", "_humanRaceToggle", "_vulcanRaceToggle", "_androidRaceToggle" })
    Check($"  CustomizationManager.{f}", creatorType?.GetField(f, Inst));
Check("  HandAnimatorManager.get_Instance / NotifyAnimatorsForConsole", game.GetType("HandAnimatorManager") is { } ham
    && ham.GetMethod("get_Instance", PS) is not null && ham.GetMethod("NotifyAnimatorsForConsole", Inst) is not null ? ham : null);
Check("  CameraFocusArea.RecacheChildHandInteractions", game.GetType("CameraFocusArea")?.GetMethod("RecacheChildHandInteractions", Inst));
var photonPlayer = game.GetType("CBBCCGMALMP");
Check("  photon player isLocal / get_customProperties / SetCustomProperties", photonPlayer?.GetField("isLocal", Inst) is not null
    && photonPlayer.GetMethod("get_customProperties", Inst) is not null && photonPlayer.GetMethod("SetCustomProperties", Inst) is not null ? photonPlayer : null);

// Mission maps (Last Stand on E3, [Debug] MapOverride)
Check("Loader.GetMapName / GetLoadedMap", game.GetType("Loader") is { } loaderType && loaderType.GetMethod("GetMapName", Inst) is not null
    && loaderType.GetMethod("GetLoadedMap", Inst) is not null ? loaderType : null);
foreach (var m in new[] { "AMAMFJDNMKB" })
    Check($"  MissionManager.{m} (seeded Random)", missionManager?.GetMethod(m, Inst));
foreach (var f in new[] { "GBEFMAACMFK", "DJFMMNBHJPC" })
    Check($"  MissionManager.{f}", missionManager?.GetField(f, Inst));
Check("  MissionManager base .LFBBCGCLOKG (sector map)", missionManager?.BaseType?.GetField("LFBBCGCLOKG", Inst) ?? missionManager?.GetField("LFBBCGCLOKG", Inst));
Check("  Sector.MapBase.ChooseStartingSystem", game.GetType("Sector.MapBase")?.GetMethod("ChooseStartingSystem", Inst));

Console.WriteLine(failures == 0 ? "\nAll targets resolved." : $"\n{failures} target(s) missing.");
return failures == 0 ? 0 : 1;
