using UnityEngine;
using Unity.Netcode;
using System.Collections;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// LiveItemSyncClient v1.2
///
/// ИСПРАВЛЕНО v1.2:
///   - CS0103: добавлен "using Unity.Netcode" — NetworkManager не находился.
///   - CS1061: убран StackCount — его нет в ItemInstance.
///     Стаки считаем вручную: количество ItemInstance с одинаковым Definition.
///   - CS0618: FindObjectOfType → FindFirstObjectByType.
/// </summary>
public class LiveItemSyncClient : MonoBehaviour
{
    [Header("Настройки")]
    [SerializeField] private float  syncIntervalSeconds   = 10f;
    [SerializeField] private string fastApiUrl            = "http://localhost:8000";
    [SerializeField] private int    requestTimeoutSeconds = 10;

    private int           _sessionId;
    private ItemInventory _inventory;
    private float         _timer;

    public void SetSessionId(int sessionId) => _sessionId = sessionId;
    public void SetFastApiUrl(string url)   { if (!string.IsNullOrEmpty(url)) fastApiUrl = url; }

    void Start()
    {
        var netObj = GetComponent<NetworkObject>();
        if (netObj != null && !netObj.IsOwner)
        {
            enabled = false;
            return;
        }

        _inventory = GetComponent<ItemInventory>();
        if (_inventory == null)
        {
            Debug.LogWarning("[LiveItemSyncClient] ItemInventory не найден — скрипт отключён");
            enabled = false;
        }
    }

    void Update()
    {
        if (_sessionId <= 0 || _inventory == null) return;

        _timer += Time.deltaTime;
        if (_timer >= syncIntervalSeconds)
        {
            _timer = 0f;
            StartCoroutine(SendItemsSnapshot());
        }
    }

    private IEnumerator SendItemsSnapshot()
    {
        var items = _inventory.GetAllItems();
        if (items == null || items.Count == 0) yield break;

        // ИСПРАВЛЕНО: using Unity.Netcode решает CS0103
        if (NetworkManager.Singleton == null)
        {
            Debug.LogWarning("[LiveItemSyncClient] NetworkManager.Singleton == null, пропускаем");
            yield break;
        }

        long clientId = (long)NetworkManager.Singleton.LocalClientId;

        // ИСПРАВЛЕНО: ItemInstance не имеет StackCount.
        // Считаем количество вхождений одного ItemDefinition в список вручную.
        var stackCounts = new Dictionary<string, int>();
        var defById     = new Dictionary<string, ItemDefinition>();

        foreach (var item in items)
        {
            if (item?.Definition == null) continue;
            string id = item.Definition.itemId;
            if (!stackCounts.ContainsKey(id))
            {
                stackCounts[id] = 0;
                defById[id]     = item.Definition;
            }
            stackCounts[id]++;
        }

        var snapshot = new LiveItemSyncRequest
        {
            session_id = _sessionId,
            client_id  = clientId,
            items      = new List<LiveItemData>()
        };

        foreach (var kv in stackCounts)
        {
            var def = defById[kv.Key];
            snapshot.items.Add(new LiveItemData
            {
                item_id     = def.itemId,
                item_name   = def.displayName,
                item_rarity = def.rarity.ToString(),
                quantity    = kv.Value
            });
        }

        string json = SerializeToJson(snapshot);
        yield return PostCoroutine(json);
    }

    private IEnumerator PostCoroutine(string json)
    {
        string url     = $"{fastApiUrl}/live/items_update";
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

        using var req = new UnityEngine.Networking.UnityWebRequest(url, "POST");
        req.uploadHandler   = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = requestTimeoutSeconds;

        yield return req.SendWebRequest();

        if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
            Debug.Log("[LiveItemSyncClient] ✅ Предметы синхронизированы");
        else
            Debug.LogWarning($"[LiveItemSyncClient] ⚠️ Ошибка: {req.error}");
    }

    private string SerializeToJson(LiveItemSyncRequest req)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        sb.AppendFormat("\"session_id\":{0},", req.session_id);
        sb.AppendFormat("\"client_id\":{0},",  req.client_id);
        sb.Append("\"items\":[");

        for (int i = 0; i < req.items.Count; i++)
        {
            if (i > 0) sb.Append(",");
            var it = req.items[i];
            sb.Append("{");
            sb.AppendFormat("\"item_id\":\"{0}\",",     Escape(it.item_id));
            sb.AppendFormat("\"item_name\":\"{0}\",",   Escape(it.item_name));
            sb.AppendFormat("\"item_rarity\":\"{0}\",", Escape(it.item_rarity));
            sb.AppendFormat("\"quantity\":{0}",         it.quantity);
            sb.Append("}");
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n");
    }
}

[System.Serializable]
public class LiveItemSyncRequest
{
    public int                session_id;
    public long               client_id;
    public List<LiveItemData> items = new List<LiveItemData>();
}
