using System;
using System.Collections.Generic;
using System.Linq;
using StbcServer.Net;
using StbcServer.Protocol;

namespace StbcServer.Game;

public sealed class LobbyKey : IEquatable<LobbyKey>
{
    public static readonly LobbyKey Default = new("", LobbyType.Default);

    public LobbyKey(string name, LobbyType type)
    {
        Name = name;
        Type = type;
    }

    public string Name { get; }
    public LobbyType Type { get; }

    public bool Equals(LobbyKey? other) => other != null && other.Name == Name && other.Type == Type;
    public override bool Equals(object? obj) => Equals(obj as LobbyKey);
    public override int GetHashCode() => Name.GetHashCode() * 31 + (int)Type;
    public static bool operator ==(LobbyKey? a, LobbyKey? b) => ReferenceEquals(a, b) || (a is not null && a.Equals(b));
    public static bool operator !=(LobbyKey? a, LobbyKey? b) => !(a == b);
    public override string ToString() => Name.Length == 0 && Type == LobbyType.Default ? "default" : $"{Name}/{Type}";
}

public sealed class Actor
{
    public Actor(int actorNr, Connection connection, ClientSession session, PHashtable properties)
    {
        ActorNr = actorNr;
        Connection = connection;
        Session = session;
        Properties = properties;
    }

    public int ActorNr { get; }
    public Connection Connection { get; }
    public ClientSession Session { get; }
    public PHashtable Properties { get; }
    public HashSet<byte> Groups { get; } = new();
    public bool AllGroups { get; set; }

    public bool InGroup(byte group) => group == 0 || AllGroups || Groups.Contains(group);
}

public sealed class CachedEvent
{
    public CachedEvent(int actorNr, byte code, object? data, bool global)
    {
        ActorNr = actorNr;
        Code = code;
        Data = data;
        Global = global;
    }

    public int ActorNr { get; }
    public byte Code { get; }
    public object? Data { get; }
    public bool Global { get; }
}

/// <summary>A game room. All access happens under the server lock.</summary>
public sealed class Room
{
    private int nextActorNr = 1;

    public Room(string name, string appVersion, LobbyKey lobby)
    {
        Name = name;
        AppVersion = appVersion;
        Lobby = lobby;
    }

    public string Name { get; }
    public string AppVersion { get; }
    public LobbyKey Lobby { get; }
    public DateTime Created { get; } = DateTime.UtcNow;

    /// <summary>Room properties: custom string keys plus the well-known byte keys (<see cref="GameProp"/>).</summary>
    public PHashtable Properties { get; } = new();

    public SortedDictionary<int, Actor> Actors { get; } = new();
    public List<CachedEvent> EventCache { get; } = new();
    public bool CleanupCacheOnLeave { get; set; } = true;
    public bool SuppressRoomEvents { get; set; }
    public bool PublishUserId { get; set; }

    /// <summary>The crew password (set by the host's mod; never sent to anyone). Null: none.</summary>
    public string? Password { get; set; }

    public int TakeActorNr() => nextActorNr++;

    public byte MaxPlayers => MaxPlayersOf(Properties);
    public bool IsOpen => Properties.GetOrDefault(GameProp.IsOpen) is not false;
    public bool IsVisible => Properties.GetOrDefault(GameProp.IsVisible) is not false;
    public bool IsFull => MaxPlayers > 0 && Actors.Count >= MaxPlayers;
    public int PlayerCount => Actors.Count;

    public static byte MaxPlayersOf(PHashtable props) => props.GetOrDefault(GameProp.MaxPlayers) switch
    {
        byte b => b,
        int i => (byte)Math.Max(0, Math.Min(i, 255)),
        short s => (byte)Math.Max(0, Math.Min((int)s, 255)),
        _ => 0,
    };

    /// <summary>The master from room property 248 (always set once someone's in; null if it names a player who left).</summary>
    public int? ExplicitMasterClientId =>
        Properties.GetOrDefault(GameProp.MasterClientId) is int id && Actors.ContainsKey(id) ? id : null;

    /// <summary>PUN 1.71 picks the lowest actor number unless the room property overrides it.</summary>
    public int MasterClientId => ExplicitMasterClientId ?? (Actors.Count > 0 ? Actors.Keys.First() : 0);

    public int[] ActorList => Actors.Keys.ToArray();

    public PHashtable ActorPropertiesTable()
    {
        var all = new PHashtable(Actors.Count);
        foreach (var a in Actors.Values) all[a.ActorNr] = a.Properties;
        return all;
    }

    /// <summary>What the lobby room list shows: listed custom props plus max/open/player count.</summary>
    public PHashtable LobbyProperties() => LobbyPropertiesOf(Properties, PlayerCount);

    public static PHashtable LobbyPropertiesOf(PHashtable props, int playerCount)
    {
        var p = new PHashtable();
        if (props.GetOrDefault(GameProp.PropsListedInLobby) is string[] listed)
            foreach (var key in listed)
                if (props.TryGetValue(key, out var v))
                    p[key] = v;
        p[GameProp.MaxPlayers] = MaxPlayersOf(props);
        p[GameProp.IsOpen] = props.GetOrDefault(GameProp.IsOpen) is not false;
        p[GameProp.PlayerCount] = (byte)Math.Min(playerCount, 255);
        return p;
    }

    /// <summary>Merge changed properties; a null value removes the key.</summary>
    public static void Merge(PHashtable target, PHashtable changes)
    {
        foreach (var kv in changes)
        {
            if (kv.Value == null) target.Remove(kv.Key);
            else target[kv.Key] = kv.Value;
        }
    }

    /// <summary>Compare-and-swap check: every expected key must currently hold exactly that value.</summary>
    public static bool MatchesExpected(PHashtable current, PHashtable expected)
    {
        foreach (var kv in expected)
        {
            current.TryGetValue(kv.Key, out var actual);
            if (!Protocol16.SameValue(actual, kv.Value)) return false;
        }
        return true;
    }

    // ---- Event cache -------------------------------------------------------------------------

    public void RemoveCachedEvents(byte code, int[]? actors, object? dataFilter)
    {
        EventCache.RemoveAll(e =>
            (code == 0 || e.Code == code)
            && (actors == null || actors.Contains(e.ActorNr))
            && DataMatches(e.Data, dataFilter));
    }

    public void RemoveCachedEventsOfLeftActors() =>
        EventCache.RemoveAll(e => !e.Global && !Actors.ContainsKey(e.ActorNr));

    private static bool DataMatches(object? data, object? filter)
    {
        if (filter is not PHashtable f || f.Count == 0) return true;
        if (data is not PHashtable d) return false;
        foreach (var kv in f)
        {
            if (!d.TryGetValue(kv.Key, out var v)) return false;
            if (!Protocol16.SameValue(v, kv.Value)) return false;
        }
        return true;
    }

    // ---- Delivery ----------------------------------------------------------------------------

    public void Broadcast(EventData ev, Actor? except = null)
    {
        var body = Serialize(ev);
        foreach (var a in Actors.Values)
            if (a != except) a.Connection.SendEventBody(body);
    }

    public static byte[] Serialize(EventData ev)
    {
        var w = new Writer();
        Protocol16.WriteEvent(w, ev);
        return w.ToArray();
    }

    public List<Actor> Receivers(Actor sender, ReceiverGroup group, int[]? targets, byte interestGroup)
    {
        IEnumerable<Actor> candidates;
        if (targets != null)
            candidates = targets.Select(n => Actors.GetOrDefault(n)).Where(a => a != null).Select(a => a!);
        else if (group == ReceiverGroup.All)
            candidates = Actors.Values;
        else if (group == ReceiverGroup.MasterClient)
            candidates = Actors.TryGetValue(MasterClientId, out var m) ? new[] { m } : new Actor[0];
        else
            candidates = Actors.Values.Where(a => a != sender);
        return candidates.Where(a => a.InGroup(interestGroup)).ToList();
    }
}
