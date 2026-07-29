using System;
using System.Collections.Generic;

// ══════════════════════════════════════════════════════════════════════════════
// LIVE MATCH DATA — модели для JSON сериализации
// Отправляются с Dedicated Server на FastAPI каждые 5 секунд
//
// ИСПРАВЛЕНО v1.1:
//   - client_id: int → long (OwnerClientId — это ulong, int переполнялся)
// ══════════════════════════════════════════════════════════════════════════════

[Serializable]
public class LiveWeaponData
{
    public int    slot_index;
    public string weapon_id;
    public string weapon_name;
    public int    weapon_level;
}

[Serializable]
public class LiveTotemData
{
    public int    slot_index;
    public string totem_id;
    public string totem_name;
    public string bonus_type;
    public int    totem_level;
    public float  totem_value;
}

[Serializable]
public class LiveItemData
{
    public string item_id;
    public string item_name;
    public string item_rarity;
    public int    quantity;
}

[Serializable]
public class LivePlayerSnapshot
{
    public long   client_id;       // ИСПРАВЛЕНО: был int — переполнение при ulong OwnerClientId
    public string username;
    public int    character_index;
    public int    level;
    public int    kills;
    public float  health;
    public float  max_health;
    public float  shield;
    public List<LiveWeaponData> weapons = new List<LiveWeaponData>();
    public List<LiveTotemData>  totems  = new List<LiveTotemData>();
    public List<LiveItemData>   items   = new List<LiveItemData>();
}

[Serializable]
public class LiveMatchUpdateRequest
{
    public int    session_id;
    public string server_id;
    public int    match_time_seconds;
    public List<LivePlayerSnapshot> players = new List<LivePlayerSnapshot>();
}
