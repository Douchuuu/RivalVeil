using UnityEngine;
using Unity.Netcode;
using VContainer;

/// <summary>
/// EnemyHealth — исправленная версия с интеграцией предметов.
///
/// ═══════════════════════════════════════════════════════════════
/// ФИКС E-4 — двойной Destroy AcidDebuffComponent:
///   Заменили Destroy(acidDebuff) на acidDebuff.enabled = false.
///
/// ИСПРАВЛЕНИЕ: GetComponent&lt;ItemInventory&gt; убран из горячего пути TakeDamage/Die.
///   БЫЛО:
///     var inventory = attacker.GetComponent&lt;ItemInventory&gt;();  ← каждый удар!
///   СТАЛО:
///     var inventory = attacker.GetItemInventory();             ← возвращает кэш из PlayerStats
///
///   PlayerStats.GetItemInventory() возвращает закешированный компонент.
///   При 700 врагах в melee: 700 GetComponent → 700 вызовов кэша (O(1)).
///
/// ИНТЕГРАЦИЯ ПРЕДМЕТОВ:
///   • TakeDamage → attacker.GetItemInventory().NotifyPlayerHitEnemy() — Челюсть (вампиризм)
///   • Die        → attacker.GetItemInventory().NotifyPlayerKillEnemy() — Голова Демона
///   • Die        → TrySpawnBonusOrb — Академичная Шляпа (доп. орб опыта)
///
/// ВАЖНО: NotifyPlayerHitEnemy вызывается через GetItemInventory() —
///   любое оружие которое вызывает EnemyHealth.TakeDamage(amount, playerStats)
///   автоматически триггерит механики предметов без дополнительных изменений.
/// ═══════════════════════════════════════════════════════════════
/// </summary>
public class EnemyHealth : MonoBehaviour
{
    [Header("Характеристики")]
    public float maxHealth = 30f;
    public float currentHealth;

    private float _baseMaxHealth;

    [Header("Лут (Опыт)")]
    [Tooltip("Сколько XP даёт враг при смерти")]
    public int xpAmount = 10;

    [Tooltip("(Legacy) Префаб XP орба — используется только если ExperienceOrbPool недоступен")]
    public GameObject xpOrbPrefab;

    private bool _isDying = false;
    private ExperienceOrbPool _orbPool;
    private MonoBehaviour _bossAI;

    // ─── VCONTAINER ИНЪЕКЦИЯ ────────────────────────────────────────────────
    [Inject]
    public void Construct(ExperienceOrbPool orbPool)
    {
        _orbPool = orbPool;
    }

    private void Awake()
    {
        _baseMaxHealth = maxHealth;
        _bossAI = GetComponent("MiniBossAI") as MonoBehaviour;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СБРОС (для пула)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ФИКС E-4: enabled = false вместо Destroy для AcidDebuffComponent.
    /// Также уведомляем AcidPuddle о сбросе (убираем из глобального кулдауна).
    /// </summary>
    public void ResetState()
    {
        currentHealth = maxHealth;
        _isDying = false;

        var acidDebuff = GetComponent<AcidDebuffComponent>();
        if (acidDebuff != null)
        {
            acidDebuff.ResetDebuff();
            acidDebuff.enabled = false;
        }

        // ИСПРАВЛЕНИЕ: убираем этот враг из глобального кулдауна AcidPuddle.
        // Без этого переиспользованный (pooled) враг мог быть неуязвим к кислоте
        // в течение TICK_RATE секунд после повторного появления.
        AcidPuddle.RemoveCooldownForEnemy(this);
    }

    private void OnEnable() => ResetState();

    // ─────────────────────────────────────────────────────────────────────────
    // УРОН
    // ─────────────────────────────────────────────────────────────────────────

    public void TakeDamage(float amount, PlayerStats attacker = null)
    {
        if (_isDying) return;

        // ── ИНТЕГРАЦИЯ: Челюсть (вампиризм при ударе) ────────────────────────
        // ИСПРАВЛЕНИЕ: используем GetItemInventory() вместо GetComponent.
        // GetItemInventory() возвращает кешированную ссылку из PlayerStats — O(1).
        if (attacker != null)
        {
            var inventory = attacker.GetItemInventory();
            inventory?.NotifyHitEnemy(this);
        }

        currentHealth -= amount;

        if (currentHealth <= 0)
        {
            _isDying = true;
            Die(attacker);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СМЕРТЬ
    // ─────────────────────────────────────────────────────────────────────────

    private void Die(PlayerStats attacker = null)
    {
        // Определяем владельца орбов — клиент убившего игрока
        // ulong.MaxValue = без владельца (SinglePlayer или legacy-спавн без аттакера)
        ulong orbOwner = ulong.MaxValue;
        if (attacker != null && attacker.IsSpawned)
            orbOwner = attacker.OwnerClientId;

        // 1. ОБЫЧНЫЙ СПАВН ОПЫТА
        SpawnXpOrb(transform.position, false, orbOwner);

        // 2. БОНУСНЫЙ ОРБ (Академичная Шляпа) — каскадный шанс
        if (attacker != null)
            TrySpawnBonusOrb(attacker, transform.position, orbOwner);

        // 3. УВЕДОМЛЕНИЕ ОБ УБИЙСТВЕ (Голова Демона — стак урона)
        // ИСПРАВЛЕНИЕ: GetItemInventory() вместо GetComponent.
        if (attacker != null)
        {
            var inventory = attacker.GetItemInventory();
            inventory?.NotifyEnemyKilled();
        }

        // 4. СЕТЕВАЯ ЛОГИКА УБИЙСТВА
        if (attacker != null)
        {
            if (attacker.IsOwner)
            {
                attacker.AddKill();
                attacker.ValidateKillWithServer(gameObject.GetInstanceID());
            }
            else if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                attacker.NotifyKillClientRpc(gameObject.GetInstanceID());
            }
        }

        // 5. ВОЗВРАТ В ПУЛ
        ReturnToPool();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СПАВН ОРБОВ
    // ─────────────────────────────────────────────────────────────────────────

    private void SpawnXpOrb(Vector3 position, bool isBonus, ulong ownerClientId = ulong.MaxValue)
    {
        if (_orbPool != null)
        {
            if (isBonus)
                _orbPool.GetBonusOrb(position, xpAmount, ownerClientId);
            else
                _orbPool.GetOrb(position, xpAmount, ownerClientId);
        }
        else if (xpOrbPrefab != null)
        {
            Instantiate(xpOrbPrefab, position, Quaternion.identity);
        }
    }

    /// <summary>
    /// Академичная Шляпа: каскадный шанс на доп. орб.
    /// Использует GetItemInventory() — без GetComponent.
    /// </summary>
    private void TrySpawnBonusOrb(PlayerStats attacker, Vector3 position, ulong ownerClientId = ulong.MaxValue)
    {
        if (attacker == null) return;

        // ИСПРАВЛЕНИЕ: GetItemInventory() вместо GetComponent.
        var inventory = attacker.GetItemInventory();
        if (inventory == null) return;

        var hats = inventory.GetItemsWithEffect(ItemEffectType.ExtraExpOrbChance);
        if (hats == null || hats.Count == 0) return;

        for (int i = 0; i < hats.Count; i++)
        {
            float chance = 0f;
            foreach (var effect in hats[i].Definition.effects)
            {
                if (effect.type == ItemEffectType.ExtraExpOrbChance)
                {
                    chance = effect.value / 100f;
                    break;
                }
            }

            if (chance <= 0f) continue;

            if (Random.value < chance)
            {
                SpawnXpOrb(position + Random.insideUnitSphere * 0.5f, true, ownerClientId);
                Debug.Log($"[EnemyHealth] 🎓 Шляпа #{i + 1}/{hats.Count} сработала! (шанс {chance * 100f:F0}%)");
                return;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void ReturnToPool()
    {
        // ИЗМЕНЕНИЕ: ищем EnemyPool в своей сцене вместо глобального Instance.
        // В Additive Scene режиме каждый игрок имеет свой EnemyPool.
        // Статический Instance.gameObject может быть в другой сцене → краш.
        EnemyPool pool = FindPoolInMyScene();
        if (pool == null) { Destroy(gameObject); return; }

        if (_bossAI != null) pool.ReturnMiniBoss(gameObject);
        else pool.ReturnEnemy(gameObject);
    }

    /// <summary>
    /// Находит EnemyPool в той же сцене что и этот враг.
    /// В Additive Scene режиме у каждого игрока свой пул в своей сцене.
    /// Статический EnemyPool.Instance может указывать на чужой пул.
    /// </summary>
    private EnemyPool FindPoolInMyScene()
    {
        // Быстрый путь: Instance уже в нужной сцене (80% случаев в Single Player)
        if (EnemyPool.Instance != null &&
            EnemyPool.Instance.gameObject.scene == gameObject.scene)
            return EnemyPool.Instance;

        // Медленный путь: ищем среди корневых объектов своей сцены
        // Вызывается редко — только при конфликте двух пулов в Competitive
        var roots = gameObject.scene.GetRootGameObjects();
        foreach (var root in roots)
        {
            var pool = root.GetComponentInChildren<EnemyPool>(true);
            if (pool != null) return pool;
        }

        Debug.LogWarning("[EnemyHealth] EnemyPool не найден в сцене! Fallback на Instance.");
        return EnemyPool.Instance;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API
    // ─────────────────────────────────────────────────────────────────────────

    public void SetMaxHealth(float val)
    {
        maxHealth     = val;
        currentHealth = val;
    }

    public float GetCurrentHealth()  => currentHealth;
    public float GetMaxHealth()      => maxHealth;
    public float GetBaseMaxHealth()  => _baseMaxHealth;
}
