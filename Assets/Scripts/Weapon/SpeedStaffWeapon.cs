using UnityEngine;
using Unity.Netcode;
using VContainer;
using System.Collections.Generic;

/// <summary>
/// SpeedStaffWeapon v3 — убран legacy IUpgradeable, FindObjectsByType → EnemyPool.
///
/// Единственный путь апгрейда: IDynamicUpgradeable.ApplyDynamicUpgrade()
///
/// Поддерживаемые статы:
///   Damage          — % к базовому урону
///   AttackSpeed     — % к скорости атаки
///   ProjectileCount — +N снарядов за атаку (бьёт по N ближайшим)
///   CritChance      — + абс. шанс крита (через StatSheet)
///   CritMultiplier  — + множитель крита (через StatSheet)
///   ProjectileSpeed — % к скорости полёта снаряда
///
/// Особенности:
///   • Пассивный бонус скорости игрока при экипировке (passiveSpeedMultiplier)
///   • Крит передаётся в StatSheet через StatModifier (источник "speed_staff")
///   • FindTargets использует EnemyPool вместо FindObjectsByType — без просадок FPS
/// </summary>
public class SpeedStaffWeapon : MonoBehaviour, IDynamicUpgradeable
{
    [Header("🔮 БАЗОВЫЕ ХАРАКТЕРИСТИКИ ПОСОХА")]
    [SerializeField] private float baseDamage = 15f;
    [SerializeField] private float baseAttackCooldown = 1.2f;
    [SerializeField] private float baseProjectileSpeed = 18f;
    [SerializeField] private int basePiercingCount = 0;
    [SerializeField] private int baseProjectileCount = 1;

    [Header("🚀 ПАССИВНЫЙ БОНУС СКОРОСТИ")]
    [Tooltip("Множитель к скорости передвижения при экипировке (1.1 = +10%)")]
    [SerializeField] private float passiveSpeedMultiplier = 1.1f;

    [Header("📦 СНАРЯД")]
    [SerializeField] private GameObject projectilePrefab;

    // ── Текущие характеристики ────────────────────────────────────────────────
    private float _currentDamage;
    private float _currentAttackCooldown;
    private float _currentProjectileSpeed;
    private int _currentPiercingCount;
    private int _currentProjectileCount;

    // ── Накопленные бонусы ────────────────────────────────────────────────────
    private float _bonusDamagePct = 0f;
    private float _bonusAttackSpeedPct = 0f;
    private float _bonusProjSpeedPct = 0f;
    private int _bonusExtraPiercing = 0;
    private float _bonusExtraProjectiles = 0f;

    // Крит-бонусы посоха — накапливаются и передаются в StatSheet
    private float _totalCritChanceBonus = 0f;
    private float _totalCritMultiplierBonus = 0f;

    // ── Состояние ─────────────────────────────────────────────────────────────
    private float _attackTimer = 0f;
    private bool _speedBonusApplied = false;

    private NetworkBehaviour _playerNetwork;
    private PlayerStats _playerStats;
    private PlayerMovement _playerMovement;
    private CombatCalculator _combatCalc;
    private EnemyPool _enemyPool;

    private const string STAT_SOURCE = "speed_staff";

    // ─── INJECTION ────────────────────────────────────────────────────────────

    [Inject]
    public void Construct(CombatCalculator combatCalc, EnemyPool enemyPool)
    {
        _combatCalc = combatCalc;
        _enemyPool = enemyPool;
        Debug.Log("[SpeedStaffWeapon] ✅ Зависимости инжектированы");
    }

    // ─────────────────────────────────────────────────────────────────────────

    void Awake() => RecalcStats();

    void Start()
    {
        _playerNetwork = GetComponentInParent<NetworkBehaviour>();
        _playerStats = GetComponentInParent<PlayerStats>();
        _playerMovement = GetComponentInParent<PlayerMovement>();
        ApplySpeedBonus();
        Debug.Log($"[SpeedStaffWeapon] Init: DMG={_currentDamage:F1}, CD={_currentAttackCooldown:F2}, " +
                  $"ProjSpeed={_currentProjectileSpeed:F1}, Pierce={_currentPiercingCount}, Proj={_currentProjectileCount}");
    }

    void OnDestroy()
    {
        RemoveSpeedBonus();
        RemoveCritModifiers();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПЕРЕСЧЁТ
    // ─────────────────────────────────────────────────────────────────────────

    void RecalcStats()
    {
        _currentDamage = baseDamage * (1f + _bonusDamagePct / 100f);
        _currentAttackCooldown = baseAttackCooldown / (1f + _bonusAttackSpeedPct / 100f);
        _currentProjectileSpeed = baseProjectileSpeed * (1f + _bonusProjSpeedPct / 100f);
        _currentPiercingCount = Mathf.Max(0, basePiercingCount + _bonusExtraPiercing);
        _currentProjectileCount = Mathf.Max(1, baseProjectileCount + Mathf.FloorToInt(_bonusExtraProjectiles));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПАССИВНЫЙ БОНУС СКОРОСТИ
    // ─────────────────────────────────────────────────────────────────────────

    private void ApplySpeedBonus()
    {
        if (_speedBonusApplied || _playerMovement == null) return;
        _playerMovement.walkSpeed *= passiveSpeedMultiplier;
        _playerMovement.maxAirSpeed *= passiveSpeedMultiplier;
        _speedBonusApplied = true;
    }

    private void RemoveSpeedBonus()
    {
        if (!_speedBonusApplied || _playerMovement == null) return;
        _playerMovement.walkSpeed /= passiveSpeedMultiplier;
        _playerMovement.maxAirSpeed /= passiveSpeedMultiplier;
        _speedBonusApplied = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // КРИТ-МОДИФИКАТОРЫ В STATSHEET
    // ─────────────────────────────────────────────────────────────────────────

    private void ApplyCritModifiers()
    {
        if (_playerStats == null) return;
        RemoveCritModifiers();

        if (_totalCritChanceBonus > 0f)
            _playerStats.AddStatModifier(new StatModifier(
                StatType.CritChance, _totalCritChanceBonus, ModifierType.Additive, source: STAT_SOURCE));

        if (_totalCritMultiplierBonus > 0f)
            _playerStats.AddStatModifier(new StatModifier(
                StatType.CritMultiplier, _totalCritMultiplierBonus, ModifierType.Additive, source: STAT_SOURCE));
    }

    private void RemoveCritModifiers()
    {
        _playerStats?.RemoveStatModifiersFromSource(STAT_SOURCE);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПРОВЕРКА ЛОКАЛЬНОГО ИГРОКА
    // ─────────────────────────────────────────────────────────────────────────

    private bool IsLocalPlayer()
    {
        // Dedicated Server не является локальным игроком
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && !NetworkManager.Singleton.IsHost) return false;
        if (GameModeManager.IsMode(GameMode.SinglePlayer)) return _playerStats != null;
        return _playerNetwork != null && _playerNetwork.IsOwner;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЦИКЛ АТАКИ
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!IsLocalPlayer()) return;
        if (_playerStats?.IsAttackBlocked == true) return;

        _attackTimer -= Time.deltaTime;
        if (_attackTimer <= 0f) TryAttack();
    }

    void TryAttack()
    {
        float attackSpeedMult = _playerStats?.StatSheet?.GetStat(StatType.AttackSpeed) ?? 1f;
        float effectiveCooldown = _currentAttackCooldown / attackSpeedMult;
        _attackTimer = effectiveCooldown;

        List<EnemyHealth> targets = FindTargets(_currentProjectileCount);
        if (targets.Count == 0) { Debug.Log("[SpeedStaffWeapon] Нет целей."); return; }

        foreach (var target in targets) FireProjectile(target);

        Debug.Log($"🔮 Посох! Снарядов={targets.Count}, DMG={_currentDamage:F1}, CD={effectiveCooldown:F2}s");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОИСК ЦЕЛЕЙ — через EnemyPool вместо FindObjectsByType
    // ─────────────────────────────────────────────────────────────────────────

    private List<EnemyHealth> FindTargets(int maxCount)
    {
        Vector3 origin = _playerStats != null ? _playerStats.transform.position : transform.position;

        IReadOnlyList<EnemyHealth> allActive = _enemyPool != null
            ? _enemyPool.GetActiveEnemyHealths()
            : (IReadOnlyList<EnemyHealth>)System.Array.Empty<EnemyHealth>();

        var sorted = new List<(float dist, EnemyHealth enemy)>(allActive.Count);
        foreach (var e in allActive)
        {
            if (e == null || !e.gameObject.activeInHierarchy) continue;
            sorted.Add((Vector3.Distance(origin, e.transform.position), e));
        }
        sorted.Sort((a, b) => a.dist.CompareTo(b.dist));

        var result = new List<EnemyHealth>(maxCount);
        for (int i = 0; i < sorted.Count && result.Count < maxCount; i++)
            result.Add(sorted[i].enemy);
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СОЗДАНИЕ СНАРЯДА
    // ─────────────────────────────────────────────────────────────────────────

    private void FireProjectile(EnemyHealth target)
    {
        if (projectilePrefab == null)
        {
            Debug.LogError("[SpeedStaffWeapon] projectilePrefab не назначен!");
            return;
        }

        Quaternion spawnRot = Quaternion.identity;
        if (target != null)
        {
            Vector3 dir = target.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
                spawnRot = Quaternion.LookRotation(dir.normalized);
        }

        // ProjectileSpeedMultiplier — глобальный множитель скорости снарядов персонажа.
        // finalSpeed = weapon.projSpeed × char.ProjectileSpeedMultiplier × (1 + upgrade%)
        float charProjSpeedMult = _playerStats?.StatSheet?.GetStat(StatType.ProjectileSpeedMultiplier) ?? 1f;
        float finalSpeed        = _currentProjectileSpeed * charProjSpeedMult;

        GameObject projObj = Instantiate(projectilePrefab, transform.position, spawnRot);
        SpeedStaffProjectile proj = projObj.GetComponent<SpeedStaffProjectile>();

        if (proj == null)
        {
            Debug.LogError("[SpeedStaffWeapon] SpeedStaffProjectile не найден на префабе!");
            Destroy(projObj);
            return;
        }

        proj.Init(target, _currentDamage, finalSpeed,
                  _currentPiercingCount, _playerStats, _combatCalc);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDynamicUpgradeable
    // ─────────────────────────────────────────────────────────────────────────

    public float? GetCurrentStatValue(StatBonusType stat)
    {
        switch (stat)
        {
            case StatBonusType.Damage: return _currentDamage;
            case StatBonusType.AttackSpeed: return _currentAttackCooldown;
            case StatBonusType.ProjectileCount: return _currentProjectileCount;
            case StatBonusType.Piercing: return _currentPiercingCount;
            case StatBonusType.ProjectileSpeed: return _currentProjectileSpeed;
            case StatBonusType.CritChance:
                return _playerStats?.StatSheet != null
                    ? _playerStats.StatSheet.GetRawCritChance() * 100f
                    : (float?)null;
            case StatBonusType.CritMultiplier:
                return _playerStats?.StatSheet != null
                    ? _playerStats.StatSheet.GetStat(StatType.CritMultiplier)
                    : (float?)null;
            default: return null;
        }
    }

    public StatBonusType[] GetAvailableStats() => new[]
    {
        StatBonusType.Damage,
        StatBonusType.AttackSpeed,
        StatBonusType.ProjectileCount,
        StatBonusType.Piercing,
        StatBonusType.CritChance,
        StatBonusType.CritMultiplier,
        StatBonusType.ProjectileSpeed,
    };

    public void ApplyDynamicUpgrade(WeaponUpgradeOffer offer)
    {
        Debug.Log($"⬆️ [SpeedStaffWeapon] {offer.Rarity} апгрейд — ДО: DMG={_currentDamage:F1}");

        foreach (var bonus in offer.Bonuses)
        {
            switch (bonus.type)
            {
                case StatBonusType.Damage:
                    _bonusDamagePct += bonus.value; break;
                case StatBonusType.AttackSpeed:
                    _bonusAttackSpeedPct += bonus.value; break;
                case StatBonusType.ProjectileSpeed:
                    _bonusProjSpeedPct += bonus.value; break;
                case StatBonusType.ProjectileCount:
                    _bonusExtraProjectiles += bonus.value; break;
                case StatBonusType.Piercing:
                    _bonusExtraPiercing += (int)bonus.value; break;
                case StatBonusType.CritChance:
                    // Не зажимаем — overflow конвертируется в CritMult в PlayerStatSheet
                    _totalCritChanceBonus += bonus.value / 100f; break;
                case StatBonusType.CritMultiplier:
                    _totalCritMultiplierBonus += bonus.value; break;
            }
        }

        RecalcStats();
        ApplyCritModifiers();

        Debug.Log($"⬆️ [SpeedStaffWeapon] Готово — DMG={_currentDamage:F1}, Proj={_currentProjectileCount}");
    }

    // ─────────────────────────────────────────────────────────────────────────

    public float GetDamage() => _currentDamage;
    public float GetAttackCooldown() => _currentAttackCooldown;
    public float GetProjectileSpeed() => _currentProjectileSpeed;
    public int GetPiercingCount() => _currentPiercingCount;
    public int GetProjectileCount() => _currentProjectileCount;
    public float GetPassiveSpeedBonus() => passiveSpeedMultiplier;

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Vector3 origin = _playerStats != null ? _playerStats.transform.position : transform.position;
        Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.7f);
        Gizmos.DrawWireSphere(transform.position, 0.3f);

        var targets = FindTargets(_currentProjectileCount);
        Gizmos.color = Color.cyan;
        foreach (var t in targets)
            if (t != null) Gizmos.DrawLine(origin, t.transform.position);
    }
#endif
}