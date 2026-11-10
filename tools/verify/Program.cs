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
Check("Mission.DebugExplorationMode.DebugExplorationModeSequence (Free Roam)", game.GetType("Mission.DebugExplorationMode.DebugExplorationModeSequence"));
// Patrol
var objectiveContext = game.GetType("Mission.Scripting.Framework.IHKAJDDPCIB");
foreach (var m in new[] { "get_ActiveSystem", "FindSystemDifferentThan", "FindAllBodyWithTags", "AddStep" })
    Check($"  objective context {m} (Patrol)", objectiveContext?.GetMethods(Inst).FirstOrDefault(x => x.Name == m));
var moveTo = game.GetType("Mission.Scripting.Framework.Tasks.FNICGFPIEOL")?.GetProperty("LAADENAGNCO", Inst)?.PropertyType;
Check("  Task.LAADENAGNCO (AIMoveTo) TellAI / ToDestination (escort convoy)", moveTo?.GetMethods(Inst).FirstOrDefault(x => x.Name == "TellAI") is not null
    && moveTo.GetMethods(Inst).FirstOrDefault(x => x.Name == "ToDestination") is not null ? moveTo : null);
Check("  Sector.DIENHALJFIO (ITargetable) SetSubsystemKnown / GetUnknownSubsystemCount (research scans)", game.GetType("Sector.DIENHALJFIO") is { } itarget
    && itarget.GetMethod("SetSubsystemKnown") is not null && itarget.GetMethod("GetUnknownSubsystemCount") is not null ? itarget : null);
Check("  Sector.NHGGGDMADKM.Science / All (research scans)", game.GetType("Sector.NHGGGDMADKM") is { } sys && sys.GetField("Science") is not null && sys.GetField("All") is not null ? sys : null);
Check("  Sector.NHGGGDMADKM.Life (rescue life signs)", game.GetType("Sector.NHGGGDMADKM")?.GetField("Life"));
Check("  Sector.CargoTypes.People (rescue survivors)", game.GetType("Sector.CargoTypes")?.GetField("People"));
Check("  SystemTransform.Position / BaseName / System", game.GetType("SystemTransform") is { } st && st.GetProperty("Position") is not null
    && st.GetProperty("BaseName") is not null && st.GetProperty("System") is not null ? st : null);
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

Check("NPCHailingView._characterPrefab (game dump)", game.GetType("NPCHailingView")?.GetField("_characterPrefab", Inst));
Check("  NPCHailingManager._hailingViews", game.GetType("NPCHailingManager")?.GetField("_hailingViews", Inst));

var faceCustomization = game.GetType("FaceCustomization");
foreach (var f in new[] { "LookMaterialNames", "_originalLookIdx" })
    Check($"FaceCustomization.{f} (Romulan face)", faceCustomization?.GetField(f, Inst));
Check("CharacterLightingInterface.AOAGAPLFIEO (Klingon head: its renderer list)", game.GetType("CharacterLightingInterface")?.GetField("AOAGAPLFIEO", Inst));
Check("  FaceCustomization.SetDefaultLook", faceCustomization?.GetMethod("SetDefaultLook", Inst));
Check("  FaceCustomization.DNNOHHKPGDA / CAJFLMIJGLL (face material swaps)", faceCustomization?.GetMethod("DNNOHHKPGDA", Inst) is not null
    && faceCustomization.GetMethod("CAJFLMIJGLL", Inst) is not null ? faceCustomization : null);
Check("  CustomizationManager.FFJOIFLJFPF (creator avatars)", game.GetType("CustomizationManager")?.GetField("FFJOIFLJFPF", Inst));
Check("  NovaPlayerManager.get_Instance + PlayerManager.CharacterProfiles", game.GetType("NovaPlayerManager")?.GetMethod("get_Instance", PS) is not null
    && game.GetType("SVR.PlayerManager")?.GetField("CharacterProfiles", Inst) is { } cpf ? cpf : null);
var creatorType = game.GetType("CustomizationManager");
foreach (var m in new[] { "OGEINMNDPMI", "KPAOOOCNEKP", "PHHOOCMCLIJ", "DADHMIIKIME" })
    Check($"  CustomizationManager.{m} (ROMULAN and KLINGON choices)", creatorType?.GetMethod(m, Inst));
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
foreach (var f in new[] { "ENGIPFEKJAK", "ABBGEGBEKIF" })
    Check($"  MissionManager.{f}", missionManager?.GetField(f, Inst));
Check("  MissionManager.FindSpawnPoint (static)", missionManager?.GetMethod("FindSpawnPoint", PS));
Check("  GPNFBMFLKFP.Contract (runner contract)", game.GetType("Mission.Scripting.Framework.GPNFBMFLKFP") is { } runnerBase
    ? (object)runnerBase.GetField("Contract", Inst) ?? runnerBase.GetProperty("Contract", Inst) : null);
Check("  ObjectiveTableEntry.Encounter", game.GetType("Mission.Tables.ObjectiveTableEntry")?.GetField("Encounter", Inst));
Check("  EncounterContractSettings.ApplyEncounterSettingsToContract", game.GetType("Mission.Scripting.Encounter.EncounterContractSettings")?.GetMethod("ApplyEncounterSettingsToContract", Inst));
var encounterSetup = game.GetType("Mission.Scripting.Encounter.INHGNBIPNHB");
const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
foreach (var f in new[] { "MJDNNOGMKFG", "AHNEIJMLBCH", "LFBBCGCLOKG", "<HBMGPABJGIK>k__BackingField" })
    Check($"  INHGNBIPNHB.{f}", encounterSetup?.GetField(f, SF));
Check("  INHGNBIPNHB.JMEJBKFNKDA (reset)", encounterSetup?.GetMethod("JMEJBKFNKDA", SF));
Check("  CEOFAJJJCAN.LNIBLGBKLGI / AJPIEMDPKDC.JFAIKAOOPMO / EncounterSystem.System",
    game.GetType("Mission.Scripting.Encounter.CEOFAJJJCAN")?.GetProperty("LNIBLGBKLGI", Inst) is not null
    && game.GetType("Mission.Scripting.Encounter.AJPIEMDPKDC")?.GetProperty("JFAIKAOOPMO", Inst) is not null
    && game.GetType("Mission.Scripting.Encounter.EncounterSystem") is { } es && ((object)es.GetField("System", Inst) ?? es.GetProperty("System", Inst)) is not null ? es : null);
Check("  Sector.MapBase.ChooseStartingSystem", game.GetType("Sector.MapBase")?.GetMethod("ChooseStartingSystem", Inst));

// Briefing: the mission category toggles' handlers (quiet during the console's refresh)
foreach (var m in new[] { "MEOMCEOEBAC", "MBHACAKGEGM" })
    Check($"  ConsoleBriefingRoom.{m}(bool) (category toggle handler)", game.GetType("ConsoleBriefingRoom")?.GetMethods(Inst).FirstOrDefault(x => x.Name == m && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType.Name == "Boolean"));
// Fleet Battle: player ships take less damage
foreach (var ty in new[] { "Sector.Spacecraft.Subsystems.HullServer", "Sector.Spacecraft.Subsystems.SubsystemServer" })
    Check($"  {ty.Substring(ty.LastIndexOf('.') + 1)}.systemHit(float) (Fleet Battle armour)", game.GetType(ty)?.GetMethods(Inst | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == "systemHit" && x.GetParameters().Length == 1));
// Host hand-off
Check("  NetworkStatus.SetError(error, popup, ...) (hand-off: no popup for failed regroup joins)",
    game.GetType("SVR.Network.NetworkStatus")?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "SetError" && m.GetParameters().Length == 4));
Check("  PAFNPDODDCM/OHDEDPBGFFE.None (popup mode)", game.GetType("SVR.Network.PAFNPDODDCM+OHDEDPBGFFE")?.GetField("None"));
Check("PhotonNetwork.get_playerList (host hand-off)", photon?.GetMethod("get_playerList", PS));
var roomType = photon?.GetMethod("get_room", PS)?.ReturnType;
foreach (var m in new[] { "get_maxPlayers", "get_open", "get_visible", "get_propertiesListedInLobby", "get_customProperties", "get_name" })
    Check($"  Room.{m}", roomType?.GetMethod(m, Inst));
var playerType = photon?.GetMethod("get_player", PS)?.ReturnType;
foreach (var m in new[] { "get_ID", "get_name", "get_customProperties", "SetCustomProperties" })
    Check($"  PhotonPlayer.{m}", playerType?.GetMethod(m, Inst));

// Voice commands (prototype)
var vrmType = game.GetType("VoiceRecognitionManager");
foreach (var m in new[] { "get_UsingVoiceRecognition", "Update", "CurrentRoleIsCaptain", "IPEGCHGIGBI", "CBNKANAPHCI", "OAJJFCNDHJK" })
    Check($"VoiceRecognitionManager.{m} (voice commands)", vrmType?.GetMethod(m, Inst));
// Voice "set course for <system>": the helm's warp map
var warpPanel = game.GetType("WarpPanel");
var warpLocation = game.GetType("WarpMapLocation");
Check("  WarpPanel.SetWarpDestination (voice course)", warpPanel?.GetMethod("SetWarpDestination", Inst));
Check("  WarpPanel.HBKEIMBBMHI (current location)", warpPanel?.GetMethod("HBKEIMBBMHI", Inst));
foreach (var f in new[] { "JBDJGIKEICO", "KHDENOMKIJE" }) Check($"  WarpPanel.{f} (locations, console)", warpPanel?.GetField(f, Inst));
Check("  WarpMapLocation.<JOEKIOLDNAF>k__BackingField (system)", warpLocation?.GetField("<JOEKIOLDNAF>k__BackingField", Inst));
Check("  WarpMapLocation.KGPNEONLCJN (pickable)", warpLocation?.GetProperty("KGPNEONLCJN", Inst));
// Voice "impulse to <place>": the mission's open places, the captain's map pick, the impulse manager
Check("  MissionManager.MLPNEABKIJD (POIAvailable)", game.GetType("Mission.MissionManager")?.GetProperty("MLPNEABKIJD", Inst));
Check("  ConsoleShipInterface.INBICICINJG (POITarget)", game.GetType("ConsoleShipInterface")?.GetProperty("INBICICINJG", Inst));
Check("  NEGGPIBDOPI(body, station) (map target picked)", game.GetType("NEGGPIBDOPI")?.GetConstructors(Inst).FirstOrDefault(c => c.GetParameters().Length == 2));
Check("  CIFDOFLFGEA.Send (event bus)", game.GetType("CIFDOFLFGEA")?.GetMethod("Send", AnyS));
Check("  BaseSpacecraft.GCOAODPDBEI (engines)", game.GetType("Sector.Spacecraft.BaseSpacecraft")?.GetProperty("GCOAODPDBEI", Inst));
foreach (var prop in new[] { "PBBNLCBNGHH", "MCNGOGECDLC" })
    Check($"  impulse manager {prop} (plotted, destination)", game.GetType("Sector.Spacecraft.POLCIGDAIKK")?.GetProperty(prop, Inst));
// PvP: the other captain console off your viewscreen, ship checks as their own ship, a destroyed ship off the maps
var captain = game.GetType("ConsoleCaptain");
Check("  ConsoleCaptain.SetViewScreen", captain?.GetMethod("SetViewScreen", Inst));
foreach (var m in new[] { "BPPOGPIHPOC", "BIDAEFBJNJC", "GDMNDIOHHMN" }) Check($"  ConsoleCaptain.{m} (viewscreen cameras, material, camera update)", captain?.GetMethods(Inst).FirstOrDefault(x => x.Name == m && x.DeclaringType == captain));
Check("  ConsoleCaptain.OBPHPIAELAB (view mode)", captain?.GetField("OBPHPIAELAB", Inst));
Check("  PlayerSpacecraft.Update (subsystem alerts)", game.GetType("Sector.Spacecraft.PlayerSpacecraft")?.GetMethod("Update", Inst | BindingFlags.DeclaredOnly));
Check("  SectorBody.PMPGNBBGOLI (show on nav map)", game.GetType("Sector.SectorBody")?.GetMethod("PMPGNBBGOLI", Inst));
Check("  Sector.EEKAKKNBAIH(body) (body died)", game.GetType("Sector.EEKAKKNBAIH")?.GetConstructors(Inst).FirstOrDefault(c => c.GetParameters().Length == 1));
var pooled = game.GetType("SVR.Network.PLPKPDFOLDL");
// PvP: each crew's own space, and impulse for both ships (PvpSpace)
var simSpace = game.GetType("SimulationSpace");
foreach (var f in new[] { "JKOEGLOEGKD", "PFHOOFGPFMM", "GOMEHHDMBGH", "ENKPDCIALPO", "GCBHEHCMONM", "MKHNNJBFKPH", "CPEGBLPAMIF", "UnitToMeterScale", "UnitToSkyboxScale" })
    Check($"  SimulationSpace.{f}", simSpace?.GetField(f, Inst | BindingFlags.Static));
foreach (var m in new[] { "get_FloatingOrigin", "get_ImpulseOverride", "KPKBCKPCLDG", "get_ImpulseCooldownSynced", "EEAIEGFANPN", "Multicast_ResyncFloatingOrigin", "NetworkDeserialize", "FixedUpdate" })
    Check($"  SimulationSpace.{m}", simSpace?.GetMethods(Inst | BindingFlags.Static).FirstOrDefault(x => x.Name == m));
Check("  SimulationSpace.NNOJBJMFJPJ (ShiftID)", simSpace?.GetProperty("NNOJBJMFJPJ", AnyS));
var shipState = simSpace?.GetNestedType("CBAACGKKEDM", BindingFlags.NonPublic | BindingFlags.Public);
Check("  SimulationSpace/CBAACGKKEDM(double) (ShipState)", shipState?.GetConstructors(Inst).FirstOrDefault(c => c.GetParameters().Length == 1));
foreach (var f in new[] { "Pos", "Rot" }) Check($"  ShipState.{f}", shipState?.GetField(f, Inst));
Check("  StateBuffer.RecordState", simSpace?.GetField("ENKPDCIALPO", Inst)?.FieldType.GetMethod("RecordState", Inst));
Check("  SVR.Network.NetworkTime.TimeInSeconds", game.GetType("SVR.Network.GFFJGHDGIFB")?.GetMethod("TimeInSeconds", AnyS));
var nsb = game.GetType("NetworkSectorBody");
var nsbState = nsb?.GetNestedType("JLDAPIFNCII", BindingFlags.NonPublic | BindingFlags.Public);
foreach (var f in new[] { "ShiftId", "Position", "Flags" }) Check($"  NetworkSectorBody/State.{f}", nsbState?.GetField(f, Inst));
Check("  NetworkSectorBody.PMLPAFENGBE (ApplyState)", nsb?.GetMethod("PMLPAFENGBE", Inst));
var sysTransform = game.GetType("SystemTransform");
Check("  SystemTransform.Deserialize", sysTransform?.GetMethod("Deserialize", Inst));
foreach (var f in new[] { "_shiftId", "_trackingStartPosition", "_newTrackingStartPosition", "_trackingInSkybox", "_changeToApply", "_lastSyncTime" })
    Check($"  SystemTransform.{f}", sysTransform?.GetField(f, Inst));
var syncManager = game.GetType("SyncPointManager");
foreach (var m in new[] { "SyncWithOtherPlayers", "ClearSyncPoint", "PlayerReachedSyncPointRPC", "COHPFJGKNCF", "PNPLACHMLIP" })
    Check($"  SyncPointManager.{m}", syncManager?.GetMethods(Inst).FirstOrDefault(x => x.Name == m));
Check("  SyncPointManager.KHOCLLMDFFF (sync points)", syncManager?.GetField("KHOCLLMDFFF", Inst));
var syncPoint = syncManager?.GetNestedType("NKOICHOJBOC", BindingFlags.NonPublic | BindingFlags.Public);
foreach (var f in new[] { "PlayersSynced", "Synced" }) Check($"  SyncPoint.{f}", syncPoint?.GetField(f, Inst));
var impulseManager = game.GetType("Sector.Spacecraft.POLCIGDAIKK");
Check("  ImpulseManager.Update", impulseManager?.GetMethod("Update", Inst));
Check("  ImpulseManager.JCIEFGHGMMP (its ship)", impulseManager?.GetField("JCIEFGHGMMP", Inst));
foreach (var prop in new[] { "KANLFOJDHPL", "HHBBMNCINCA" }) Check($"  ImpulseManager.{prop} (phase, operating)", impulseManager?.GetProperty(prop, Inst));
Check("  engines.UpdateImpulse", game.GetType("Sector.Spacecraft.Subsystems.SimpleEngine")?.GetMethod("UpdateImpulse", Inst));
Check("  PooledReader.LLPAKGEPJJP (Pos)", pooled?.GetProperty("LLPAKGEPJJP", Inst));
// After a station repair: the subsystem alarm stopped, the Enterprise-D's lighting and backwall terminal back (LastStandBridgeRepair)
var tngLighting = game.GetType("TNGBridgeLightingManager");
foreach (var f in new[] { "DNHINGPBHIF", "_auxiliaryPowerDamagePercent", "_auxiliaryPowerCeilingLightColor", "CCJAGAHKINP", "ODNFNELKJDL", "FPCJABMGCHB", "_backwallScienceTerminalMesh" })
    Check($"  TNGBridgeLightingManager.{f}", tngLighting?.GetField(f, Inst));
Check("  TNGBridgeLightingManager.StartLightingEvent", tngLighting?.GetMethod("StartLightingEvent", Inst));
var bridgeLighting = game.GetType("BridgeLightingManager");
foreach (var f in new[] { "_reflectionProbe", "_defaultReflectionsMap", "_redAlertReflectionsMap", "_defaultCharacterLight1", "_defaultCharacterLight2", "_defaultCharacterLight3", "_redAlertCharacterLight1", "_redAlertCharacterLight2", "_redAlertCharacterLight3" })
    Check($"  BridgeLightingManager.{f}", bridgeLighting?.GetField(f, Inst));
Check("  BridgeLightingManager.CNNEPNDIIHM (red alert)", bridgeLighting?.GetProperty("CNNEPNDIIHM", Inst));
var shipSounds = game.GetType("PlayerSpacecraftSoundManager");
Check("  PlayerSpacecraftSoundManager.PlaySubsystemAlarmRepaired", shipSounds?.GetMethod("PlaySubsystemAlarmRepaired", Inst));
foreach (var f in new[] { "_subsystemAlarmRepairPercentage", "IPPNEKGMGCF" }) Check($"  PlayerSpacecraftSoundManager.{f}", shipSounds?.GetField(f, Inst));
Check("  PersistentBridgeDamageEffect._bridgeLightingEventId", game.GetType("PersistentBridgeDamageEffect")?.GetField("_bridgeLightingEventId", Inst));
// Patrol: leaving after a completed assignment is a success (RevivalMissions.EndRequest_Prefix)
Check("  MissionManager.IEMAFLJNBBM (end mission request)", game.GetType("Mission.MissionManager")?.GetMethod("IEMAFLJNBBM", Inst));
Check("  Mission.BEEEGBFNGFJ.MissionStatus", game.GetType("Mission.BEEEGBFNGFJ")?.GetField("MissionStatus", Inst));
// Direct voice (DirectVoice), crew logs (CrewLogs) and the performance log (PerfLog)
var networkCore = game.GetType("SVR.Network.NetworkCore");
Check("  NetworkCore.SendEvent (voice frames out)", networkCore?.GetMethods(Inst).FirstOrDefault(m => m.Name == "SendEvent" && m.GetParameters().Length == 5));
foreach (var m in new[] { "Acquire", "Release", "Write", "Seek" }) Check($"  PooledReader.{m}", pooled?.GetMethods(Inst | BindingFlags.Static).FirstOrDefault(x => x.Name == m));
foreach (var prop in new[] { "PICFPDJILEK", "CDBPFHDELDP" }) Check($"  PooledReader.{prop} (length, buffer)", pooled?.GetProperty(prop, Inst));
var voipManager = game.GetType("VoIpManager");
Check("  VoIpManager.GMFCCIFGKDN (Photon event in)", voipManager?.GetMethod("GMFCCIFGKDN", Inst));
Check("  VoIpManager.PFKNMPPKOFP (players by actor)", voipManager?.GetField("PFKNMPPKOFP", Inst));
var photonNetwork = game.GetType("NDIOGIGMPBH");
Check("  PhotonNetwork.RaiseEvent", photonNetwork?.GetMethod("RaiseEvent", AnyS));
Check("  PhotonNetwork.OnEventCall", photonNetwork?.GetField("OnEventCall", AnyS));
Check("  PhotonNetwork.PGJDPPDBGBL (inRoom)", photonNetwork?.GetProperty("PGJDPPDBGBL", AnyS));
foreach (var prop in new[] { "KLNDGCGHADM", "GPICMMEIAPM", "PCDMCKFFBAC" }) Check($"  PhotonPlayer.{prop} (ID, name, properties)", game.GetType("CBBCCGMALMP")?.GetProperty(prop, Inst));
Check("  NKEPKEMCLNH(bool) (voice button event)", game.GetType("NKEPKEMCLNH")?.GetConstructors(Inst).FirstOrDefault(c => c.GetParameters().Length == 1));
var commandManager = game.GetType("AI.CommandManager");
foreach (var m in new[] { "TriggerCommand", "UpdateSimpleOrders", "PlayInvalidCommandVO" })
    Check($"  AI.CommandManager.{m}", commandManager?.GetMethod(m, PS));
foreach (var c in new[] { "AnalyseTarget", "ArmTorpedoes", "CeaseFire", "CeaseTargeting", "CloakHunt", "DisarmTorpedoes", "DisruptComms", "DisruptEngines", "DisruptShields", "DisruptWeapons", "EngageImpulse", "EngageTarget", "EngageWarp", "FirePhasers", "FireTorpedoes", "LowerShields", "ModulateShields", "MoveAwayFrom", "MoveTo", "OptimizeBalanced", "OptimizeEngines", "OptimizeScanners", "OptimizeShields", "OptimizeTransporter", "OptimizeWeapons", "OptimzeComputerCore", "PowerBalanced", "PowerToEngines", "PowerToPhasers", "PowerToShields", "PrepareImpulse", "PrepareWarp", "RaiseShields", "ReduceSignature", "RepairBalanced", "RepairComputerCore", "RepairEngines", "RepairPhasers", "RepairScience", "RepairSensors", "RepairShields", "RepairSickbay", "RepairTorpedoes", "RepairTransporter", "RepairWarpcore", "ScanTarget", "SetSpeedFull", "SetSpeedHalf", "SetSpeedQuarter", "SetSpeedReverse", "SetSpeedStop", "TargetEngines", "TargetShields", "TargetWeapons", "TransportFromTarget" })
    Check($"  crew command {c}", game.GetType("AI.BHPPPGKNAHK")?.GetField(c));
foreach (var t in new[] { "GEJIEIDGDBO", "MJNNGHIKMIO", "DCDOEBCMNHK" })
    Check($"  voice UI event {t}", game.GetType(t));

// Voice "approach target": the helm menu's Move To entries
Check("  CommandManager.IEFBKOMBFGO (instance)", (MemberInfo)commandManager?.GetField("IEFBKOMBFGO", AnyS) ?? commandManager?.GetProperty("IEFBKOMBFGO", AnyS));
// Voice orders from any station (CrewOrders): which station carries an order out, and yours
Check("  CommandManager.LHPHCBLOOLE (an order's station)", commandManager?.GetMethod("LHPHCBLOOLE", AnyS));
var stationPlayer = game.GetType("NovaPlayerInfo");
Check("  NovaPlayerInfo.GHJLPNMIOAJ (console)", (MemberInfo)stationPlayer?.GetField("GHJLPNMIOAJ", Inst) ?? stationPlayer?.GetProperty("GHJLPNMIOAJ", Inst));
Check("  CommandManager.ENLHIAIPDMD (menus)", commandManager?.GetField("ENLHIAIPDMD", Inst | AnyS));
Check("  CommandManager.ECNLCPFAEGF (roles)", commandManager?.GetProperty("ECNLCPFAEGF", AnyS));
Check("  menus GetHelmMenu", commandManager?.GetField("ENLHIAIPDMD", Inst | AnyS)?.FieldType.GetMethod("GetHelmMenu"));
var menuItem = game.GetType("AI.CHJEFCBPHGN");
foreach (var m in new[] { "ICJLNIICKNH", "ENDEAOOGKOL", "OKKPPMGDKAP", "LLMAEPCPLGP", "BLFHNLLDICM" })
    Check($"  menu item {m}", menuItem?.GetProperty(m, Inst));
foreach (var m in new[] { "OnUpdateMenuState", "OnExecute" })
    Check($"  menu item {m}", menuItem?.GetMethod(m, Inst));

// Revival settings (the shuttle's region page)
var mainMenu = game.GetType("ConsoleMainMenu");
foreach (var m in new[] { "PKOIDGNDHFO", "GLFCDJFLJDK", "BJNFFMDAOGH", "FMNEJELNGFC" })
    Check($"ConsoleMainMenu.{m} (revival settings)", mainMenu?.GetMethod(m, Inst));
foreach (var f in new[] { "CKGKIBEGPOA", "_regionSelectButton", "_regionSelectGO", "_regionSelectRegionText", "_regionAcceptBtn" })
    Check($"  ConsoleMainMenu.{f}", mainMenu?.GetField(f, Inst));
Check("  menu state SetState(Main)", mainMenu?.GetField("CKGKIBEGPOA", Inst)?.FieldType.GetMethod("SetState", Inst) is MethodInfo set
    && Enum.GetNames(set.GetParameters()[0].ParameterType).Contains("Main") ? set : null);

// Stellar Evacuation: the E3 star's eruption animation
Check("StarManager.SetAnimTrigger (stellar evacuation)", game.GetType("StarManager")?.GetMethod("SetAnimTrigger", Inst));

// Steam friend add (escape menu player options)
var playerList = game.GetType("PlayerListUI");
foreach (var m in new[] { "EOJBLIEBGKE", "IIGLKLICKDJ" })
    Check($"PlayerListUI.{m} (steam friend add)", playerList?.GetMethod(m, Inst));
foreach (var f in new[] { "PCPBPMKFJOE", "JEFHDNFCPLD" })
    Check($"  PlayerListUI.{f}", playerList?.GetField(f, Inst));
var novaInfo = game.GetType("NovaPlayerInfo");
Check("  NovaPlayerInfo.PKIBEAIHJNE", (MemberInfo)novaInfo?.GetField("PKIBEAIHJNE", Inst) ?? novaInfo?.GetProperty("PKIBEAIHJNE", Inst));

// Translation (the mod's texts in German): the game's text lookup and its menu language
var localization = game.GetType("SVR.Localization");
Check("SVR.Localization.GetLocalizedText(string, int) (translation)", localization?.GetMethod("GetLocalizedText", AnyS, null, new[] { mlc.CoreAssembly!.GetType("System.String")!, mlc.CoreAssembly!.GetType("System.Int32")! }, null));
var menuLanguage = localization?.GetMethod("get_MenuLanguageID", AnyS);
Check("  SVR.Localization.get_MenuLanguageID", menuLanguage);
Check("  menu language German", menuLanguage != null && Enum.GetNames(menuLanguage.ReturnType).Contains("German") ? menuLanguage : null);

// Hand skin log (issue #4): the shuttle avatar's species hands and skin tone
var characterCustomization = game.GetType("CharacterCustomization");
foreach (var m in new[] { "UpdateRaceSkinValues", "SetVisualSpeciesForMainMenuAvatar" })
    Check($"CharacterCustomization.{m} (hand skin log)", characterCustomization?.GetMethod(m, Inst));
foreach (var f in new[] { "IsMainMenuAvatar", "GPLBJJJBPOL" })
    Check($"  CharacterCustomization.{f}", characterCustomization?.GetField(f, Inst));
var bodyCustomization = game.GetType("BodyCustomization");
foreach (var f in new[] { "PDMECBLMBGB", "HandMaterialPosition" })
    Check($"  BodyCustomization.{f}", bodyCustomization?.GetField(f, Inst));

// PvP crews: eight role slots, the briefing room's ship B rows, seating, menu orders, ship lines, voice per ship
var roleManager = game.GetType("NovaRoleManager");
foreach (var f in new[] { "JIOGBBNFGMJ", "LPPEEFGBIID" })
    Check($"NovaRoleManager.{f} (pvp roles)", roleManager?.GetField(f, Inst));
foreach (var m in new[] { "Awake", "Start", "NetworkSerialize", "NetworkDeserialize" })
    Check($"  NovaRoleManager.{m}", roleManager?.GetMethod(m, Inst));
Check("  NovaRoleManager.get_Instance", roleManager?.GetMethod("get_Instance", AnyS));
var briefingRoom = game.GetType("ConsoleBriefingRoom");
Check("  ConsoleBriefingRoom._playerRoleInfos", briefingRoom?.GetField("_playerRoleInfos", Inst));
foreach (var m in new[] { "Awake", "BJBJFCFIDOA", "GLFEOIFJEHE" })
    Check($"  ConsoleBriefingRoom.{m}", briefingRoom?.GetMethod(m, Inst));
Check("  ConsoleBriefingRoom/PlayerRoleInfo.Role", briefingRoom?.GetNestedType("PlayerRoleInfo", BindingFlags.Public | BindingFlags.NonPublic)?.GetField("Role", Inst));
Check("  NovaPlayerInfo.FJMHMOOJNJG (role)", game.GetType("NovaPlayerInfo")?.GetProperty("FJMHMOOJNJG", Inst));
Check("  Station.Pawn (a station's seated pawn)", (MemberInfo)game.GetType("NovaStation")?.GetField("Pawn", Inst) ?? game.GetType("Station")?.GetField("Pawn", Inst));
var seatIterator = game.GetType("NovaSpawnManager")?.GetNestedType("<SpawnNewlyAddedPlayerOnClients>c__Iterator1F3", BindingFlags.Public | BindingFlags.NonPublic);
Check("  NovaSpawnManager seating iterator .MoveNext", seatIterator?.GetMethod("MoveNext", Inst));
Check("  NovaSpawnManager seating iterator .PNGLMGLMLJM (the player)", seatIterator?.GetField("PNGLMGLMLJM", Inst));
var stationRegistry = game.GetType("GGPMPCDFHAK");
Check("  GGPMPCDFHAK.GetStationIndex(Station)", stationRegistry?.GetMethods(AnyS).FirstOrDefault(m => m.Name == "GetStationIndex" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "FLCHMJJDKDL"));
Check("  GGPMPCDFHAK.GetFreeStationIndex", stationRegistry?.GetMethod("GetFreeStationIndex", AnyS));
Check("  CommandManager.ApplyNewOrder (pvp: menu orders to the host)", game.GetType("AI.CommandManager")?.GetMethod("ApplyNewOrder", AnyS));
var pvpMenuItem = game.GetType("AI.CHJEFCBPHGN");
Check("  AI.CHJEFCBPHGN.OnExecute", pvpMenuItem?.GetMethod("OnExecute", Inst));
Check("  AI.CHJEFCBPHGN command backing field", pvpMenuItem?.GetField("<ONOKMLAGHHI>k__BackingField", Inst));
var voPlayback = game.GetType("VOPlaybackManager");
Check("  VOPlaybackManager.PlayVO (pvp: your ship's crew lines)", voPlayback?.GetMethod("PlayVO", Inst));
Check("  VOPlaybackManager.IEFBKOMBFGO", (MemberInfo)voPlayback?.GetField("IEFBKOMBFGO", AnyS) ?? voPlayback?.GetProperty("IEFBKOMBFGO", AnyS));
Check("  Bridge.NewConsoleMessageRPC", game.GetType("Bridge")?.GetMethod("NewConsoleMessageRPC", Inst));
Check("  VoIpManager.GMFCCIFGKDN (pvp: voice chat per ship)", game.GetType("VoIpManager")?.GetMethod("GMFCCIFGKDN", Inst));

// PvP on the main menu: a PVP game mode that runs Quick match for PvP rooms (game mode 7, eight players)
var pvpMenu = game.GetType("ConsoleMainMenu");
foreach (var f in new[] { "_gameModeInfos", "FNCLHAJMFJC", "_gameModeDescriptionText" })
    Check($"ConsoleMainMenu.{f} (pvp menu)", pvpMenu?.GetField(f, Inst));
foreach (var m in new[] { "Start", "MHOIHOKNNFC", "OnPlayGame" })
    Check($"  ConsoleMainMenu.{m}", pvpMenu?.GetMethod(m, Inst));
var modeInfo = pvpMenu?.GetNestedType("GameModeInfo", BindingFlags.Public | BindingFlags.NonPublic);
Check("  ConsoleMainMenu/GameModeInfo.Mode", modeInfo?.GetField("Mode", Inst));
Check("  ConsoleMainMenu/GameModeInfo.ModeSelectionToggle", modeInfo?.GetField("ModeSelectionToggle", Inst));
Check("  OCCEGCFNLPH.BeginPublicMatchmaking(int)", game.GetType("OCCEGCFNLPH")?.GetMethods(Inst).FirstOrDefault(m => m.Name == "BeginPublicMatchmaking" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "Int32"));
Check("  NovaPlayerManager.MaxNumPlayers", game.GetType("NovaPlayerManager")?.GetField("MaxNumPlayers", Inst));

// PvP ship choice: each captain's ship, each crew's bridge
var shipLoader = game.GetType("Loader");
foreach (var f in new[] { "KHGLJDMCEFE", "LJILDCNBPAN" })
    Check($"Loader.{f} (pvp ship flags)", shipLoader?.GetField(f, Inst));
foreach (var m in new[] { "NGNOCCOOOBM", "IFNGPJDAMAF", "PFMBDEEEHHL", "KHFHCAELMKD" })
    Check($"  ConsoleBriefingRoom.{m} (ship toggles)", game.GetType("ConsoleBriefingRoom")?.GetMethod(m, Inst));
Check("  MissionManager.NJHADEBHMNN (player ship spawn)", game.GetType("Mission.MissionManager")?.GetMethod("NJHADEBHMNN", Inst));
foreach (var f in new[] { "_captainNecessaryGO", "_launchGameButton", "_selectionPanel", "_missionRecapPanel", "_gameLaunchPanel" })
    Check($"  ConsoleBriefingRoom.{f}", game.GetType("ConsoleBriefingRoom")?.GetField(f, Inst));

// Player volumes (escape menu player options: the voice volume slider down to muted)
var voip = game.GetType("VoIpManager");
Check("VoIpManager.SetVolumeForPlayer (player volumes)", voip?.GetMethod("SetVolumeForPlayer", Inst));
foreach (var f in new[] { "_minPlayerVolume", "PFKNMPPKOFP" })
    Check($"  VoIpManager.{f}", voip?.GetField(f, Inst));
Check("  VoIpManager.IEFBKOMBFGO (instance)", (MemberInfo)voip?.GetField("IEFBKOMBFGO", AnyS) ?? voip?.GetProperty("IEFBKOMBFGO", AnyS));
Check("  PlayerListUI._voiceVolumeInteractionSlider", playerList?.GetField("_voiceVolumeInteractionSlider", Inst));
Check("  SliderInteraction.SliderMinValue", game.GetType("SliderInteraction")?.GetField("SliderMinValue", Inst));
var voipInfo = voip?.GetNestedType("PIHAFAACGLJ", BindingFlags.Public | BindingFlags.NonPublic);
foreach (var f in new[] { "Player", "Volume" })
    Check($"  VoIpManager/PIHAFAACGLJ.{f}", voipInfo?.GetField(f, Inst));

// VR keyboard (join by code in the headset)
Check("Valve.VR CVRSystem.PollNextEvent (vr keyboard)", game.GetType("Valve.VR.PBMLFENDPMG")?.GetMethod("PollNextEvent", Inst));
Check("  CVROverlay.ShowKeyboard", game.GetType("Valve.VR.HADDNHEFDEE")?.GetMethod("ShowKeyboard", Inst));
Check("  CVROverlay.GetKeyboardText", game.GetType("Valve.VR.HADDNHEFDEE")?.GetMethod("GetKeyboardText", Inst));

// PvP test (two player ships)
var spawnManager = game.GetType("Sector.SectorSpawnManager");
Check("SectorSpawnManager.CJDNEDMKCJJ(SectorBody) (pvp ship announcement)", spawnManager?.GetMethod("CJDNEDMKCJJ", Inst, null, new[] { game.GetType("Sector.SectorBody") }, null));
var playerShip = game.GetType("Sector.Spacecraft.PlayerSpacecraft");
foreach (var m in new[] { "HCCIGJCMAKL", "BOAPPLHKHFP", "OPMBGEHIDBD", "NEBNOAFFOJP", "NDBKDBOMAJB", "NEPBADGDJBJ", "AEIHFBMCKMD", "GCLCHPJHNNK", "Multicast_SetThrottleAndPower" })
    Check($"  PlayerSpacecraft.{m}", playerShip?.GetMethod(m, Inst));
var targetInfo = game.GetType("Sector.TargetInfoComponent");
Check("  TargetInfoComponent.DGGJOECIFAF (team)", targetInfo?.GetProperty("DGGJOECIFAF", Inst)?.GetGetMethod());
Check("  TargetInfoComponent team backing field", targetInfo?.GetField("<BEJOMHEMMLD>k__BackingField", Inst));
Check("  TargetInfoComponent.OnPhotonSerializeView", targetInfo?.GetMethod("OnPhotonSerializeView", Inst));
Check("  TargetableComponent.GetCategory", game.GetType("Sector.TargetableComponent")?.GetMethod("GetCategory", Inst));
Check("  MapIcon.ACAGOGEPCCE (selectable)", game.GetType("MapIcon")?.GetMethod("ACAGOGEPCCE", Inst));
foreach (var m in new[] { "SetTargetableByCaptain", "SetTargetableByHelm", "SetTargetableByEngineering" })
    Check($"  TargetableComponent.{m}", game.GetType("Sector.TargetableComponent")?.GetMethod(m, Inst));
foreach (var t in new[] { "MapIcon", "NavMapIcon", "UI.ViewscreenTargetingDisplay", "Sector.Spacecraft.Subsystems.SimpleEngineClient", "Sector.Spacecraft.Subsystems.SimpleWarpCoreClient" })
    Check($"  {t} (own-ship checks)", game.GetType(t));
foreach (var f in new[] { "_playerCraftPrefabPath", "_retroPlayerCraftPrefabPath", "_TNGPlayerCraftPrefabPath" })
    Check($"  MissionManager.{f}", missionManager?.GetField(f, Inst));

var targetUi = game.GetType("TacticalTargetUI");
foreach (var m in new[] { "OPGMKEGJFIP", "FBNPCAFBGCH" })
    Check($"  TacticalTargetUI.{m} (target details)", targetUi?.GetMethod(m, Inst));
foreach (var f in new[] { "_originalBridgeMode", "_tosTargetShieldDisplay", "_shieldValueText", "_shieldValueTextTNG", "_hullValueText", "_hullValueTextTNG" })
    Check($"  TacticalTargetUI.{f}", targetUi?.GetField(f, Inst));
Check("  KHDFGIINMKJ.SetText(text, string, TNG text)", game.GetType("KHDFGIINMKJ")?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    .FirstOrDefault(m => m.Name == "SetText" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType.FullName == "System.String"));
Check("  ShipFX._explosionModel", game.GetType("ShipFX")?.GetField("_explosionModel", Inst));
Check("  SimulationSpace.BALGCFOKIPN", game.GetType("SimulationSpace")?.GetProperty("BALGCFOKIPN", BindingFlags.Static | BindingFlags.Public));

var aegisCrew = game.GetType("AI.USSAegisCrew");
Check("  USSAegisCrew.IsConsoleAI (pvp: stations of the other ship's players)", aegisCrew?.GetMethod("IsConsoleAI", Inst));
foreach (var f in new[] { "GELDBDOEIBN", "IJFDAJGLKLF", "LMFOFJEALLE" })
    Check($"  USSAegisCrew.{f} (station console)", aegisCrew?.GetField(f, Inst));
Check("  USSAegisCrew.GEFJIDCHAGC (pvp: crew setup for a ship spawned in place)", aegisCrew?.GetMethod("GEFJIDCHAGC", Inst));
Check("  USSAegisCrew.OEACLEGCMNJ (crew set up)", aegisCrew?.GetField("OEACLEGCMNJ", Inst));
foreach (var m in new[] { "HOKJHIMAFMC", "MIECHGHGMND", "OLIPCPOPKIP", "CNELGOKOEOA", "BDPCPIHNPJO", "DNGNMFLCIKO", "LAAHAHPODCA", "OnEnterSystem", "OnExplosionResultsEvent", "TakeHit" })
    Check($"  USSAegisCrew.{m} (pvp: a crew task or event, run with its ship's view)", aegisCrew?.GetMethod(m, Inst));
Check("  AI.CHJEFCBPHGN.OAEMHOPHIBJ (pvp: the crew menus' cached crew)", game.GetType("AI.CHJEFCBPHGN")?.GetField("OAEMHOPHIBJ", AnyS));
Check("  USSAegisCrew.CrewbotPlayVO (pvp: the other crew's voice)", aegisCrew?.GetMethod("CrewbotPlayVO", Inst));
Check("  MissionManager.set_MonitorPlayerHealth (pvp: ship A lost halts the mission)", game.GetType("Mission.MissionManager")?.GetMethod("set_MonitorPlayerHealth", Inst));
var console = game.GetType("ConsoleBase");
foreach (var prop in new[] { "FKKJLMLCKJI", "LPMMGGIAOCL", "BJLCFFKCFFN" })
    Check($"  ConsoleBase.{prop}", console?.GetProperty(prop, Inst));
var novaPlayer = game.GetType("NovaPlayerInfo");
foreach (var prop in new[] { "NJNHJKEPFAK", "AOLEJMABHPE", "AOMGCCGDJJC", "FAJABLKBGHN" })
    Check($"  NovaPlayerInfo.{prop}", novaPlayer?.GetProperty(prop, Inst));
foreach (var f in new[] { "VisualPawnObject", "PawnObject" })
    Check($"  NovaPlayerInfo.{f}", novaPlayer?.GetField(f, Inst));

Check("  NovaStation.Awake (pvp: second console set)", game.GetType("NovaStation")?.GetMethod("Awake", Inst));
Check("  NovaStation._consoleData", game.GetType("NovaStation")?.GetField("_consoleData", Inst));
Check("  GGPMPCDFHAK.RegisterStation", game.GetType("GGPMPCDFHAK")?.GetMethod("RegisterStation", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
foreach (var f in new[] { "NAIGHOOPLNL", "EKHOODMFJNG" })
    Check($"  GGPMPCDFHAK.{f} (station lists)", game.GetType("GGPMPCDFHAK")?.GetField(f, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
Check("  ConsoleLoader.Awake", game.GetType("ConsoleLoader")?.GetMethod("Awake", Inst));
Check("  ConsoleLoader.BGJAFEBKCPD (load queue)", game.GetType("ConsoleLoader")?.GetField("BGJAFEBKCPD", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
Check("  ConsoleBase.ILKCJBGLJPI (binds to a ship)", console?.GetMethod("ILKCJBGLJPI", Inst));
Check("  Bridge.MBCIEEJDGBB (console loaded)", game.GetType("Bridge")?.GetMethod("MBCIEEJDGBB", Inst));
Check("  Bridge.SendConsoleMessage / NewConsoleMessageRPC", game.GetType("Bridge")?.GetMethod("NewConsoleMessageRPC", Inst) != null ? game.GetType("Bridge")?.GetMethod("SendConsoleMessage", Inst) : null);
Check("  NovaSpawnManager.NJHADEBHMNN (seats a player)", game.GetType("NovaSpawnManager")?.GetMethod("NJHADEBHMNN", Inst));
foreach (var m in new[] { "NetworkSerialize", "NetworkDeserialize" })
    Check($"  UnmannedStationSync.{m}", game.GetType("UnmannedStationSync")?.GetMethod(m, Inst));

// Voice commands listen in on voice chat's recording
Check("VoIpManager.GetLocalMicrophoneInputDevice (voice commands)", game.GetType("VoIpManager")?.GetMethod("GetLocalMicrophoneInputDevice", Inst));
Check("  UnityMicrophoneInputDevice.FJNHDGFAOLB (device)", game.GetType("DaikonForge.VoIP.UnityMicrophoneInputDevice")?.GetField("FJNHDGFAOLB", Inst));
Check("  AudioInputDeviceBase.EALMEBPADKI (recording source)", game.GetType("DaikonForge.VoIP.AudioInputDeviceBase")?.GetField("EALMEBPADKI", Inst));
var wwiseMic = game.GetType("WwiseMicrophoneInputDevice");
var wwiseDevice = wwiseMic?.GetField("GMNCBFBICKK", Inst) ?? wwiseMic?.BaseType?.GetField("GMNCBFBICKK", Inst);
Check("  WwiseMicrophoneInputDevice.GMNCBFBICKK (device, Wwise voice template)", wwiseDevice);
Check("  AudioInputDeviceBase.StartRecording (voice chat's mic restarted)", game.GetType("DaikonForge.VoIP.AudioInputDeviceBase")?.GetMethod("StartRecording", Inst));

// Crew passwords
var briefingConsole = game.GetType("ConsoleBriefingRoom");
foreach (var m in new[] { "CIBAPNHJCIF", "OKIKIKEHPPC", "IACKCMNEEKK" })
    Check($"ConsoleBriefingRoom.{m} (crew password)", briefingConsole?.GetMethod(m, Inst));
Check("  ConsoleBriefingRoom.FEFMKFOMEPA (open-room button)", briefingConsole?.GetField("FEFMKFOMEPA", Inst));
Check("  SVR.Network.PBGHGLIFLAE(string) (join event)", game.GetType("SVR.Network.PBGHGLIFLAE")?.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.FullName == "System.String"));

// Blocking after a kick
Check("PlayerListUI.HIMNNIBCBPA (kick confirmed)", game.GetType("PlayerListUI")?.GetMethod("HIMNNIBCBPA", Inst));
Check("  PlayerListUI.PCPBPMKFJOE (selected player)", game.GetType("PlayerListUI")?.GetField("PCPBPMKFJOE", Inst));

// Voice jitter buffer
var voicePlayer = game.GetType("WwiseAudioPlayer");
foreach (var m in new[] { "BufferAudio", "BatchUpdate" })
    Check($"WwiseAudioPlayer.{m} (voice chat)", voicePlayer?.GetMethod(m, Inst));
foreach (var f in new[] { "NIPDKJAKHCO", "BOAFPOJBLCN" })
    Check($"  AudioPlayerBase.{f}", game.GetType("DaikonForge.VoIP.AudioPlayerBase")?.GetField(f, Inst));
Check("  VoiceControllerBase.PLHMCJMKMOC (missing frames)", game.GetType("DaikonForge.VoIP.VoiceControllerBase")?.GetField("PLHMCJMKMOC", Inst));

Check("  NovaSpawnManager.NKILJLNGDHM (role station swap)", game.GetType("NovaSpawnManager")?.GetMethod("NKILJLNGDHM", Inst));
foreach (var f in new[] { "_next", "_previous" })
    Check($"  NovaStation.{f} (station cycling)", game.GetType("NovaStation")?.GetField(f, Inst));

Check("AI.AgentShip.CPMKGMJPINE (cloak, fleet battle)", game.GetType("AI.AgentShip")?.GetMethod("CPMKGMJPINE", Inst));

// Excelsior test: holodeck bridge
foreach (var m in new[] { "get_CurrentBridgeScene", "get_HolodeckBridgeScene" })
    Check($"Loader.{m} (excelsior)", game.GetType("Loader")?.GetMethod(m, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));

// Bridge Walk (reflected names)
var nac = game.GetType("SVR.NovaAnimationController");
foreach (var m in new[] { "EEEKOHNIDBH", "FIFMENKIJFI" }) Check($"SVR.NovaAnimationController.{m} (seat turn, backrest lean)", nac?.GetMethod(m, Inst));
Check("  PawnComponent.HAMOMEFCFNB (_playerInfo)", game.GetType("PawnComponent")?.GetField("HAMOMEFCFNB", Inst));
var oec = game.GetType("OrientExternalCamera");
foreach (var f in new[] { "_pivot", "BJGOCLMLBLD", "_bridgeViewOcclusionProxy" }) Check($"  OrientExternalCamera.{f}", oec?.GetField(f, Inst));
Check("  ShuttleCameraMover.BatchLateUpdate", game.GetType("ShuttleCameraMover")?.GetMethod("BatchLateUpdate", Inst));

// Hairstyles (reflected names)
var faceCust = game.GetType("FaceCustomization");
foreach (var f in new[] { "_faceMesh", "_currentLookIdx", "LookMaterialNames" }) Check($"FaceCustomization.{f} (hairstyles)", faceCust?.GetField(f, Inst));
foreach (var m in new[] { "SetNextLook", "SetPrevLook", "HLDEKGCKKKA" }) Check($"  FaceCustomization.{m}", faceCust?.GetMethod(m, Inst, null, Type.EmptyTypes, null));
Check("  FaceCustomization.SetLook(int)", faceCust?.GetMethods(Inst).FirstOrDefault(x => x.Name == "SetLook" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType.Name == "Int32"));
var custMgr = game.GetType("CustomizationManager");
foreach (var f in new[] { "_lookText", "_headText", "IBONPLINBFL", "EPNEDLICCMJ", "COABBGKIHFL", "OGGAMIKLGBM", "PPILFEEKDAP", "JGOBPBOKGKO" }) Check($"  CustomizationManager.{f}", custMgr?.GetField(f, Inst));
foreach (var m in new[] { "ENPBOKEDLLO", "PEMPNBCOPFL" }) Check($"  CustomizationManager.{m} (static)", custMgr?.GetMethod(m, AnyS));

// VR controller grace
var inputManager = game.GetType("SVR.InputManager");
Check("SVR.InputManager.AADJJIOIJJA(bool) (controller check)", inputManager?.GetMethods(Inst).FirstOrDefault(m => m.Name == "AADJJIOIJJA" && m.GetParameters().Length == 1));
Check("  SVR.InputManager.OMHIFCDEJLK (controllers connected)", inputManager?.GetField("OMHIFCDEJLK", Inst));

// VR device order
var selectVr = game.GetType("InitialLoader")?.GetNestedTypes(BindingFlags.NonPublic).FirstOrDefault(t => t.Name.StartsWith("<SelectVRMode>"));
Check("InitialLoader/<SelectVRMode>.MoveNext (VR device)", selectVr?.GetMethod("MoveNext", Inst));
foreach (var (f, what) in new[] { ("DBNIHIBHOLC", "state"), ("EONPCBMAPBK", "VR wanted"), ("DIHFOIJGJBK", "device list"), ("ILLBDLPMFPB", "current") })
    Check($"  <SelectVRMode>.{f} ({what})", selectVr?.GetField(f, Inst));

// Ranks (reflected names)
var playerInfo = game.GetType("NovaPlayerInfo");
Check("NovaPlayerInfo.FPCLMHFIGLD setter (CurrentPlayerRank)", playerInfo?.GetProperty("FPCLMHFIGLD", Inst)?.SetMethod);
Check("BodyCustomization.OMJKMLCLMNI (worn rank)", game.GetType("BodyCustomization")?.GetField("OMJKMLCLMNI", Inst));

Console.WriteLine(failures == 0 ? "\nAll targets resolved." : $"\n{failures} target(s) missing.");
return failures == 0 ? 0 : 1;
