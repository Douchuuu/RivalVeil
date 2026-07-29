using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════
/// ITEM INVENTORY UI  (v2.1 — SinglePlayer + Competitive)
/// ══════════════════════════════════════════════════════════════════════
///
/// ИСПРАВЛЕНИЕ v2.1:
///   - OnItemAdded: добавлена проверка activeInHierarchy перед StartCoroutine
///     (исправляет ошибку "Coroutine couldn't be started because the game object is inactive")
/// </summary>
public class ItemInventoryUI : MonoBehaviour
{
    // ─── РЕЖИМ РАБОТЫ ─────────────────────────────────────────────────────────

    [Header("── Режим ───────────────────────────────────────────")]
    [Tooltip("В каком игровом режиме этот UI активен.\n" +
             "SinglePlayer → вешай на SinglePlayerUI_Canvas\n" +
             "Competitive  → вешай на CompetitiveUI_Canvas")]
    [SerializeField] private GameMode targetMode = GameMode.SinglePlayer;

    // ─── КОНТЕЙНЕР И PREFAB ───────────────────────────────────────────────────

    [Header("── Контейнер слотов ──────────────────────────────")]
    [Tooltip("Transform с HorizontalLayoutGroup + ContentSizeFitter")]
    [SerializeField] private Transform itemSlotsContainer;

    [Header("── Prefab слота ─────────────────────────────────────")]
    [Tooltip("Prefab одного слота. Структура: Image(фон) > ItemIcon, RarityBorder, CountText")]
    [SerializeField] private GameObject itemSlotPrefab;

    // ─── TOOLTIP ──────────────────────────────────────────────────────────────

    [Header("── Tooltip (опционально) ──────────────────────────")]
    [SerializeField] private GameObject      tooltipPanel;
    [SerializeField] private TextMeshProUGUI tooltipTitle;
    [SerializeField] private TextMeshProUGUI tooltipDescription;
    [SerializeField] private TextMeshProUGUI tooltipRarity;

    // ─── ВИЗУАЛ ───────────────────────────────────────────────────────────────

    [Header("── Визуальные настройки ───────────────────────────")]
    [SerializeField] private string iconChildName        = "ItemIcon";
    [SerializeField] private string rarityBorderChildName = "RarityBorder";
    [SerializeField] private string countTextChildName   = "CountText";
    [SerializeField] private bool   animateOnAdd         = true;

    // ─── ВНУТРЕННЕЕ СОСТОЯНИЕ ─────────────────────────────────────────────────

    private ItemInventory _inventory;

    private struct SlotData
    {
        public GameObject       slotGO;
        public Image            iconImage;
        public Image            rarityBorder;
        public TextMeshProUGUI  countText;
        public ItemDefinition   definition;
    }

    private readonly Dictionary<ItemDefinition, SlotData> _slots      = new Dictionary<ItemDefinition, SlotData>();
    private readonly Dictionary<ItemDefinition, int>      _stackCounts = new Dictionary<ItemDefinition, int>();

    private readonly List<ItemDefinition> _toRemove = new List<ItemDefinition>(8);

    // ─────────────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        if (tooltipPanel != null)
            tooltipPanel.SetActive(false);

        StartCoroutine(WaitForPlayer());
    }

    void OnDestroy()
    {
        Unsubscribe();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОИСК ИГРОКА
    // ─────────────────────────────────────────────────────────────────────────

    private IEnumerator WaitForPlayer()
    {
        const float RETRY_INTERVAL = 0.2f;

        while (!GameModeManager.IsMode(targetMode))
            yield return new WaitForSeconds(RETRY_INTERVAL);

        while (_inventory == null)
        {
            var allStats = FindObjectsByType<PlayerStats>(FindObjectsSortMode.None);
            foreach (var stats in allStats)
            {
                bool isLocal = stats.IsOwner ||
                               GameModeManager.IsMode(GameMode.SinglePlayer);

                if (!isLocal) continue;

                var inv = stats.GetComponent<ItemInventory>();
                if (inv != null)
                {
                    Subscribe(inv);
                    Debug.Log($"[ItemInventoryUI] ✅ [{targetMode}] Подключён к ItemInventory на '{stats.name}'");
                    yield break;
                }
            }

            yield return new WaitForSeconds(RETRY_INTERVAL);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОДПИСКА / ОТПИСКА
    // ─────────────────────────────────────────────────────────────────────────

    private void Subscribe(ItemInventory inventory)
    {
        _inventory = inventory;
        _inventory.OnItemAdded      += OnItemAdded;
        _inventory.OnItemRemoved    += OnItemRemoved;
        _inventory.OnInventoryChanged += RebuildAll;
        RebuildAll();
    }

    private void Unsubscribe()
    {
        if (_inventory == null) return;
        _inventory.OnItemAdded      -= OnItemAdded;
        _inventory.OnItemRemoved    -= OnItemRemoved;
        _inventory.OnInventoryChanged -= RebuildAll;
        _inventory = null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОБРАБОТКА СОБЫТИЙ
    // ─────────────────────────────────────────────────────────────────────────

    private void OnItemAdded(ItemInstance item)
    {
        RebuildAll();

        // ИСПРАВЛЕНИЕ: Проверяем что объект активен перед запуском корутины
        if (animateOnAdd && _slots.TryGetValue(item.Definition, out var slot))
        {
            if (gameObject.activeInHierarchy)
            {
                StartCoroutine(AnimateSlot(slot.slotGO));
            }
        }
    }

    private void OnItemRemoved(ItemInstance item)
    {
        RebuildAll();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОСТРОЕНИЕ UI
    // ─────────────────────────────────────────────────────────────────────────

    private void RebuildAll()
    {
        if (_inventory == null) 
        {
            Debug.LogWarning("[ItemInventoryUI] RebuildAll: _inventory is NULL!");
            return;
        }

        var items = _inventory.GetAllItems();
        Debug.Log($"[ItemInventoryUI] RebuildAll: {items.Count} предметов в инвентаре");

        _stackCounts.Clear();
        foreach (var item in _inventory.GetAllItems())
        {
            var def = item.Definition;
            if (!_stackCounts.ContainsKey(def))
                _stackCounts[def] = 0;
            _stackCounts[def]++;
        }

        _toRemove.Clear();
        foreach (var kv in _slots)
            if (!_stackCounts.ContainsKey(kv.Key))
                _toRemove.Add(kv.Key);

        foreach (var def in _toRemove)
        {
            if (_slots[def].slotGO != null)
                Destroy(_slots[def].slotGO);
            _slots.Remove(def);
        }

        foreach (var kv in _stackCounts)
        {
            if (_slots.ContainsKey(kv.Key))
                UpdateStackCount(_slots[kv.Key], kv.Value);
            else
                _slots[kv.Key] = CreateSlot(kv.Key, kv.Value);
        }
    }

    private SlotData CreateSlot(ItemDefinition definition, int count)
    {
        Debug.Log($"[ItemInventoryUI] CreateSlot: {definition.displayName} x{count}");
        
        if (itemSlotsContainer == null)
        {
            Debug.LogError("[ItemInventoryUI] itemSlotsContainer is NULL!");
        }
        if (itemSlotPrefab == null)
        {
            Debug.LogWarning("[ItemInventoryUI] itemSlotPrefab is NULL - using fallback!");
        }
        
        var go = itemSlotPrefab != null
            ? Instantiate(itemSlotPrefab, itemSlotsContainer)
            : CreateFallbackSlot();

        var slot = new SlotData
        {
            slotGO       = go,
            definition   = definition,
            iconImage    = FindChild<Image>(go, iconChildName),
            rarityBorder = FindChild<Image>(go, rarityBorderChildName),
            countText    = FindChild<TextMeshProUGUI>(go, countTextChildName)
        };

        if (slot.iconImage != null && definition.icon != null)
        {
            slot.iconImage.sprite = definition.icon;
            slot.iconImage.color  = Color.white;
        }

        if (slot.rarityBorder != null)
            slot.rarityBorder.color = definition.rarityColor;

        UpdateStackCount(slot, count);
        SetupTooltip(go, definition);

        return slot;
    }

    private GameObject CreateFallbackSlot()
    {
        var go = new GameObject("ItemSlot", typeof(RectTransform));
        go.transform.SetParent(itemSlotsContainer, false);

        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(64f, 64f);

        var bgImg = go.AddComponent<Image>();
        bgImg.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);

        AddChildImage(go, iconChildName,
            new Vector2(0.1f, 0.1f), new Vector2(0.9f, 0.9f));

        var borderImg = AddChildImage(go, rarityBorderChildName,
            Vector2.zero, Vector2.one);
        borderImg.color = Color.clear;

        AddChildText(go, countTextChildName);

        return go;
    }

    private Image AddChildImage(GameObject parent, string childName,
                                Vector2 anchorMin, Vector2 anchorMax)
    {
        var obj = new GameObject(childName, typeof(RectTransform));
        obj.transform.SetParent(parent.transform, false);
        var rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return obj.AddComponent<Image>();
    }

    private void AddChildText(GameObject parent, string childName)
    {
        var obj = new GameObject(childName, typeof(RectTransform));
        obj.transform.SetParent(parent.transform, false);
        var rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0.4f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var tmp = obj.AddComponent<TextMeshProUGUI>();
        tmp.alignment  = TextAlignmentOptions.BottomRight;
        tmp.fontSize   = 14f;
        tmp.color      = Color.white;
        tmp.fontStyle  = FontStyles.Bold;
    }

    private void UpdateStackCount(SlotData slot, int count)
    {
        if (slot.countText == null) return;

        if (count <= 1)
        {
            slot.countText.text    = "";
            slot.countText.enabled = false;
        }
        else
        {
            slot.countText.text    = $"×{count}";
            slot.countText.enabled = true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TOOLTIP
    // ─────────────────────────────────────────────────────────────────────────

    private void SetupTooltip(GameObject slotGO, ItemDefinition definition)
    {
        if (tooltipPanel == null) return;

        var trigger = slotGO.AddComponent<EventTrigger>();

        var enterEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        enterEntry.callback.AddListener(_ => ShowTooltip(definition));
        trigger.triggers.Add(enterEntry);

        var exitEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
        exitEntry.callback.AddListener(_ => HideTooltip());
        trigger.triggers.Add(exitEntry);
    }

    private void ShowTooltip(ItemDefinition definition)
    {
        if (tooltipPanel == null) return;
        tooltipPanel.SetActive(true);
        if (tooltipTitle != null)       tooltipTitle.text       = definition.displayName;
        if (tooltipRarity != null)      tooltipRarity.text      = definition.GetRarityLabel();
        if (tooltipDescription != null) tooltipDescription.text = definition.description;
    }

    private void HideTooltip()
    {
        if (tooltipPanel != null)
            tooltipPanel.SetActive(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // АНИМАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    private IEnumerator AnimateSlot(GameObject slotGO)
    {
        if (slotGO == null) yield break;

        var rt         = slotGO.GetComponent<RectTransform>();
        var startScale = Vector3.one;
        var punchScale = Vector3.one * 1.3f;
        const float HALF = 0.125f;

        float t = 0f;
        while (t < HALF) { t += Time.unscaledDeltaTime; rt.localScale = Vector3.Lerp(startScale, punchScale, t / HALF); yield return null; }
        t = 0f;
        while (t < HALF) { t += Time.unscaledDeltaTime; rt.localScale = Vector3.Lerp(punchScale, startScale, t / HALF); yield return null; }

        rt.localScale = startScale;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // УТИЛИТЫ
    // ─────────────────────────────────────────────────────────────────────────

    private T FindChild<T>(GameObject parent, string childName) where T : Component
    {
        var child = parent.transform.Find(childName);
        if (child != null) return child.GetComponent<T>();

        foreach (Transform t in parent.GetComponentsInChildren<Transform>())
            if (t.name == childName && t.TryGetComponent<T>(out var comp))
                return comp;

        Debug.LogWarning($"[ItemInventoryUI] '{childName}' не найден в prefab слота. " +
                         "Проверь имена дочерних объектов в Inspector.");
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПУБЛИЧНОЕ API
    // ─────────────────────────────────────────────────────────────────────────

    public void Reconnect()
    {
        Unsubscribe();

        foreach (var kv in _slots)
            if (kv.Value.slotGO != null) Destroy(kv.Value.slotGO);
        _slots.Clear();
        _stackCounts.Clear();

        StartCoroutine(WaitForPlayer());
    }
}
