using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Chest — сундук с одним детерминированным предметом.
///
/// ДЕТЕРМИНИЗМ (Competitive):
///   seed = SharedSeed ^ (openCount * prime)
///   Оба игрока получают одинаковый предмет одинаковой редкости.
///
/// SINGLE PLAYER:
///   Чистый рандом.
/// </summary>
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(Rigidbody))]
public class Chest : MonoBehaviour
{
    [Header("ССЫЛКИ")]
    [SerializeField] private ItemDatabase itemDatabase;
    [SerializeField] private ParticleSystem openParticles;
    [SerializeField] private AudioSource openSound;

    [Header("БАЛАНС РЕДКОСТЕЙ")]
    [SerializeField][Range(0f, 100f)] private float chanceCommon = 60f;
    [SerializeField][Range(0f, 100f)] private float chanceRare = 25f;
    [SerializeField][Range(0f, 100f)] private float chanceMythic = 12f;
    [SerializeField][Range(0f, 100f)] private float chanceLegendary = 3f;

    [Header("ВИЗУАЛИЗАЦИЯ")]
    [SerializeField] private MeshRenderer meshRenderer;
    [SerializeField] private Material commonMaterial;
    [SerializeField] private Material rareMaterial;
    [SerializeField] private Material mythicMaterial;
    [SerializeField] private Material legendaryMaterial;

    private bool _isOpened = false;
    private Transform _playerTransform;
    private ItemInventory _playerInventory;
    private float _interactDistance = 2.5f;

    // ── ФИКС БАГ 4: счётчик открытий привязан к сцене игрока ────────────────
    // БЫЛО: private static int _openCount = 0;
    //   В Competitive оба игрока в одном процессе делили этот счётчик.
    //   Player 1 открывает сундук → _openCount = 1.
    //   Player 2 открывает СВОЙ первый сундук → _openCount = 2 → другой seed → другой предмет.
    //   Детерминизм сломан.
    //
    // СТАЛО: ChestSpawner (один per-scene/player) предоставляет функцию
    //   GetAndIncrementOpenCount(). Каждый сундук вызывает её при открытии.
    //   В Competitive у каждого игрока свой ChestSpawner → свой счётчик.
    private System.Func<int> _getAndIncrementCount;

    /// <summary>
    /// Привязывает сундук к per-scene счётчику открытий.
    /// Вызывается из ChestSpawner.SpawnChestAtPosition() сразу после Instantiate.
    /// </summary>
    internal void Init(System.Func<int> getAndIncrementCount)
    {
        _getAndIncrementCount = getAndIncrementCount;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (itemDatabase == null)
            itemDatabase = Resources.Load<ItemDatabase>("ItemDatabase");
        if (itemDatabase == null)
            Debug.LogError("[Chest] ItemDatabase не найдена в Resources/!");

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = true; rb.constraints = RigidbodyConstraints.FreezeAll; }

        Collider col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;

        Debug.Log($"[Chest] Инициализирован на позиции {transform.position}");
    }

    private void Update()
    {
        if (!_isOpened && _playerTransform != null)
        {
            float dist = Vector3.Distance(transform.position, _playerTransform.position);
            if (dist <= _interactDistance && Input.GetKeyDown(KeyCode.E))
                Open();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    public void Open()
    {
        if (_isOpened) return;
        _isOpened = true;

        if (openSound != null) openSound.Play();
        if (openParticles != null) openParticles.Play();

        // ФИКС БАГ 4: используем per-scene счётчик из ChestSpawner.
        // Если Init() не был вызван (fallback без ChestSpawner) — используем локальный счётчик.
        int currentCount = _getAndIncrementCount != null ? _getAndIncrementCount() : 1;

        // АНТИ-ЧИТ: сообщаем серверу о новом открытии.
        // Сервер проверит что счётчик вырос ровно на +1.
        var playerStats = _playerInventory?.GetComponent<PlayerStats>();
        playerStats?.ReportChestOpenServerRpc(currentCount);
        int itemSeed;

        if (GameModeManager.IsCompetitiveMode() && GameModeManager.SharedSeed != 0)
        {
            unchecked { itemSeed = GameModeManager.SharedSeed ^ (currentCount * 1_000_003); }
            Debug.Log($"[Chest] Competitive открытие #{currentCount}, itemSeed={itemSeed}");
        }
        else
        {
            itemSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
        }

        ItemDefinition item = GenerateItem(itemSeed);

        if (item != null && _playerInventory != null)
        {
            _playerInventory.AddItem(item);
            NotifyAllItemsOfChestOpen(_playerInventory);
            Debug.Log($"[Chest] Выпал: {item.displayName} ({item.rarity})");
        }

        Destroy(gameObject, 0.5f);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private ItemDefinition GenerateItem(int seed)
    {
        if (itemDatabase == null || itemDatabase.GetAllItems().Count == 0)
        {
            Debug.LogError("[Chest] ItemDatabase пуста!");
            return null;
        }

        var rng = new System.Random(seed);

        float total = chanceCommon + chanceRare + chanceMythic + chanceLegendary;
        if (total <= 0f) total = 100f;
        float roll = (float)rng.NextDouble() * total;

        ItemRarity rarity;
        if (roll < chanceCommon) rarity = ItemRarity.Common;
        else if (roll < chanceCommon + chanceRare) rarity = ItemRarity.Rare;
        else if (roll < chanceCommon + chanceRare + chanceMythic) rarity = ItemRarity.Mythic;
        else rarity = ItemRarity.Legendary;

        var pool = itemDatabase.GetItemsByRarity(rarity);
        if (pool.Count == 0)
        {
            Debug.LogWarning($"[Chest] Нет предметов редкости {rarity}!");
            return null;
        }

        return SelectWeightedItem(rng, pool);
    }

    private ItemDefinition SelectWeightedItem(System.Random rng, List<ItemDefinition> items)
    {
        float total = 0f;
        foreach (var item in items) total += item.dropWeight;
        if (total <= 0f) total = items.Count;

        float roll = (float)rng.NextDouble() * total;
        float acc = 0f;
        foreach (var item in items)
        {
            acc += item.dropWeight;
            if (roll <= acc) return item;
        }
        return items[items.Count - 1];
    }

    private void NotifyAllItemsOfChestOpen(ItemInventory inventory)
    {
        if (inventory == null) return;
        var talismans = inventory.GetItemsWithEffect(ItemEffectType.DamageBonus);
        foreach (var t in talismans) { t.OnChestOpened(); Debug.Log("[Chest] Талисман +3% урона!"); }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            _playerTransform = other.transform;
            _playerInventory = other.GetComponent<ItemInventory>();
            Debug.Log("[Chest] Игрок подошёл. Нажми E для открытия!");
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Player")) { _playerTransform = null; _playerInventory = null; }
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, _interactDistance);
    }
}