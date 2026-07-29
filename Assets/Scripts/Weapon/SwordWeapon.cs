using UnityEngine;
using Unity.Netcode;
using VContainer;

/// <summary>
/// SwordWeapon v6 — убран legacy IUpgradeable.
///
/// Единственный путь апгрейда: IDynamicUpgradeable.ApplyDynamicUpgrade()
/// Вызывается из LevelUpManager → WeaponUpgradeOffer.Apply()
///
/// Поддерживаемые статы:
///   Damage          — % к базовому урону
///   AttackSpeed     — % к скорости атаки (уменьшает кулдаун)
///   Radius          — % к радиусу атаки
///   ProjectileCount — +N ударов за свинг
///
/// Глобальный AttackSpeed из StatSheet применяется в TryAttack() — не хранится
/// в оружии. Это позволяет бонусам от предметов (Факел и т.д.) работать без
/// пересчёта характеристик самого меча.
/// </summary>
public class SwordWeapon : MonoBehaviour, IDynamicUpgradeable
{
    [Header("⚔️ ХАРАКТЕРИСТИКИ МЕЧА")]
    [SerializeField] private float baseDamage = 20f;
    [SerializeField] private float baseAttackCooldown = 1.5f;
    [SerializeField] private float baseAttackRange = 3.5f;
    [SerializeField] private float baseAttackAngle = 180f;
    [SerializeField] private int baseHitsPerSwing = 1;

    [Header("🌟 ПОСТОЯННЫЙ ВИЗУАЛ КЛИНКА")]
    [Tooltip("ParticleSystem/GameObject который горит пока меч надет.\n" +
             "Дочерний объект: Loop=true. Prefab: включи 'Weapon Glow Is Prefab'.")]
    [SerializeField] private GameObject weaponGlowEffect;
    [SerializeField] private bool weaponGlowIsPrefab = false;

    [Header("⚡ ВСПЫШКА ПРИ УДАРЕ")]
    [Tooltip("Дуга/вспышка при каждом свинге. Loop=false, Play On Awake=false.")]
    [SerializeField] private GameObject swingArcEffect;
    [SerializeField] private bool swingArcIsPrefab = true;
    [SerializeField] private float swingArcDuration = 0.4f;
    [SerializeField] private Vector3 swingArcOffset = new Vector3(0f, 0.8f, 0f);

    [Header("💥 ХИТ-ЭФФЕКТ НА ВРАГЕ")]
    [SerializeField] private GameObject hitEffectPrefab;
    [SerializeField] private float hitEffectDuration = 0.3f;

    // ── Текущие значения (от базы + апгрейды меча) ───────────────────────────
    // Буфер для PhysicsScene.OverlapSphere
    private readonly Collider[] _swordBuffer = new Collider[64];

    private float _currentDamage;
    private float _currentAttackCooldown;  // только апгрейды меча, без глобального StatSheet
    private float _currentAttackRange;
    private float _currentAttackAngle;
    private int _currentHitsPerSwing;

    // ── Накопленные бонусы от апгрейдов меча ────────────────────────────────
    private float _bonusDamagePct = 0f;
    private float _bonusAttackSpeedPct = 0f;
    private float _bonusRadiusPct = 0f;
    private int _bonusExtraHits = 0;

    // Крит-бонусы меча — накапливаются и передаются в StatSheet игрока
    private float _totalCritChanceBonus = 0f;
    private float _totalCritMultiplierBonus = 0f;
    private const string STAT_SOURCE = "sword_weapon";

    private float _attackTimer;
    private Transform _playerTransform;
    private Transform _cam;
    private NetworkBehaviour _playerNetwork;
    private PlayerStats _playerStats;
    private CombatCalculator _combatCalc;

    private GameObject _spawnedGlow;
    private ParticleSystem _swingArcPS;

    [Inject]
    public void Construct(CombatCalculator combatCalc) => _combatCalc = combatCalc;

    // ─────────────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────────

    void Awake() => RecalcStats();

    void Start()
    {
        _playerNetwork = GetComponentInParent<NetworkBehaviour>();
        _playerStats = GetComponentInParent<PlayerStats>();
        _playerTransform = GetComponentInParent<Transform>();
        TryFindCamera();

        ApplyCritModifiers(); // применяем накопленные крит-бонусы в StatSheet

        StartWeaponGlow();
        CacheSwingArc();
    }

    void OnDestroy()
    {
        // Снимаем крит-бонусы при уничтожении оружия
        _playerStats?.RemoveStatModifiersFromSource(STAT_SOURCE);
        StopWeaponGlow();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ — GLOW
    // ─────────────────────────────────────────────────────────────────────────

    private void StartWeaponGlow()
    {
        if (weaponGlowEffect == null) return;

        GameObject glowObj;
        if (weaponGlowIsPrefab)
        {
            _spawnedGlow = Instantiate(weaponGlowEffect, transform);
            _spawnedGlow.transform.localPosition = Vector3.zero;
            _spawnedGlow.transform.localRotation = Quaternion.identity;
            glowObj = _spawnedGlow;
        }
        else
        {
            glowObj = weaponGlowEffect;
            glowObj.SetActive(true);
        }

        foreach (var ps in glowObj.GetComponentsInChildren<ParticleSystem>(true))
            if (!ps.isPlaying) ps.Play();
    }

    private void StopWeaponGlow()
    {
        if (weaponGlowIsPrefab)
        {
            if (_spawnedGlow != null) Destroy(_spawnedGlow);
        }
        else if (weaponGlowEffect != null)
        {
            foreach (var ps in weaponGlowEffect.GetComponentsInChildren<ParticleSystem>())
                ps.Stop();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ — SWING ARC
    // ─────────────────────────────────────────────────────────────────────────

    private void CacheSwingArc()
    {
        if (swingArcEffect == null || swingArcIsPrefab) return;
        _swingArcPS = swingArcEffect.GetComponent<ParticleSystem>();
    }

    private void PlaySwingArc()
    {
        if (swingArcEffect == null) return;

        Vector3 origin = (_playerTransform != null ? _playerTransform.position : transform.position)
                         + swingArcOffset;
        Quaternion rot = Quaternion.identity;
        if (_cam != null)
        {
            Vector3 dir = _cam.forward; dir.y = 0;
            if (dir.sqrMagnitude > 0.001f) rot = Quaternion.LookRotation(dir.normalized);
        }

        if (swingArcIsPrefab)
        {
            GameObject arc = Instantiate(swingArcEffect, origin, rot);
            Destroy(arc, swingArcDuration);
        }
        else if (_swingArcPS != null)
        {
            swingArcEffect.transform.SetPositionAndRotation(origin, rot);
            _swingArcPS.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _swingArcPS.Play();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ХИТ-ЭФФЕКТ
    // ─────────────────────────────────────────────────────────────────────────

    private void SpawnHitEffect(Vector3 position)
    {
        if (hitEffectPrefab == null) return;
        GameObject fx = Instantiate(hitEffectPrefab, position + Vector3.up * 0.5f, Quaternion.identity);
        Destroy(fx, hitEffectDuration);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЦИКЛ АТАКИ
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!IsLocalPlayer()) return;
        if (_playerStats?.IsAttackBlocked == true) return;
        if (_cam == null) TryFindCamera();

        _attackTimer -= Time.deltaTime;
        if (_attackTimer <= 0f) TryAttack();
    }

    void TryAttack()
    {
        for (int h = 0; h < _currentHitsPerSwing; h++)
            DealAOEDamage();

        PlaySwingArc();

        // globalSpeedMult = базовый attackSpeed персонажа + бонусы от предметов (Факел и т.д.)
        // _currentAttackCooldown = только апгрейды самого меча
        float globalSpeedMult = _playerStats?.StatSheet?.GetStat(StatType.AttackSpeed) ?? 1f;
        _attackTimer = _currentAttackCooldown / globalSpeedMult;
    }

    void DealAOEDamage()
    {
        Vector3 center = _playerTransform != null ? _playerTransform.position : transform.position;
        var physScene = gameObject.scene.GetPhysicsScene();

        // AttackRadiusMultiplier из StatSheet — глобальный множитель радиуса для персонажа.
        // Воин (1.2) → меч бьёт на 20% дальше. Маг (0.9) → меч бьёт ближе.
        // weapon.range × char.AttackRadiusMultiplier × (1 + upgrade%)
        float charRadiusMult  = _playerStats?.StatSheet?.GetStat(StatType.AttackRadiusMultiplier) ?? 1f;
        float effectiveRange  = _currentAttackRange * charRadiusMult;

        int hitCount = physScene.OverlapSphere(center, effectiveRange, _swordBuffer, ~0, QueryTriggerInteraction.Collide);
        Collider[] hits = new Collider[hitCount];
        System.Array.Copy(_swordBuffer, hits, hitCount);

        Vector3 attackDir = Vector3.forward;
        if (_cam != null)
        {
            attackDir = _cam.forward; attackDir.y = 0;
            if (attackDir.sqrMagnitude > 0.001f) attackDir.Normalize();
        }

        bool fullAOE = _currentAttackAngle >= 360f;
        float halfAngle = _currentAttackAngle * 0.5f;

        foreach (var col in hits)
        {
            EnemyHealth enemy = col.GetComponent<EnemyHealth>();
            if (enemy == null) continue;

            if (!fullAOE)
            {
                Vector3 toEnemy = col.transform.position - center;
                toEnemy.y = 0;
                if (toEnemy.sqrMagnitude > 0.001f &&
                    Vector3.Angle(attackDir, toEnemy.normalized) > halfAngle)
                    continue;
            }

            enemy.TakeDamage(CalculateDamage(enemy), _playerStats);
            SpawnHitEffect(col.transform.position);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РАСЧЁТ УРОНА
    // ─────────────────────────────────────────────────────────────────────────

    float CalculateDamage(EnemyHealth enemy = null)
    {
        if (_combatCalc != null && _playerStats != null)
            return _combatCalc.Calculate(_currentDamage, _playerStats, enemy).FinalDamage;
        return _currentDamage;
    }

    void RecalcStats()
    {
        _currentDamage = baseDamage * (1f + _bonusDamagePct / 100f);
        _currentAttackCooldown = baseAttackCooldown / (1f + _bonusAttackSpeedPct / 100f);
        _currentAttackRange = baseAttackRange * (1f + _bonusRadiusPct / 100f);
        _currentAttackAngle = baseAttackAngle;
        _currentHitsPerSwing = baseHitsPerSwing + _bonusExtraHits;
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
            case StatBonusType.Radius: return _currentAttackRange;
            case StatBonusType.ProjectileCount: return _currentHitsPerSwing;
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
        StatBonusType.Radius,
        StatBonusType.ProjectileCount,
        StatBonusType.CritChance,
        StatBonusType.CritMultiplier,
    };

    public void ApplyDynamicUpgrade(WeaponUpgradeOffer offer)
    {
        foreach (var bonus in offer.Bonuses)
        {
            switch (bonus.type)
            {
                case StatBonusType.Damage: _bonusDamagePct += bonus.value; break;
                case StatBonusType.AttackSpeed: _bonusAttackSpeedPct += bonus.value; break;
                case StatBonusType.Radius: _bonusRadiusPct += bonus.value; break;
                case StatBonusType.ProjectileCount: _bonusExtraHits += (int)bonus.value; break;
                case StatBonusType.CritChance:
                    _totalCritChanceBonus += bonus.value / 100f; break;
                case StatBonusType.CritMultiplier:
                    _totalCritMultiplierBonus += bonus.value; break;
            }
        }
        RecalcStats();
        ApplyCritModifiers();
    }

    // Передаём накопленный крит-бонус меча в StatSheet игрока
    void ApplyCritModifiers()
    {
        if (_playerStats == null) return;
        _playerStats.RemoveStatModifiersFromSource(STAT_SOURCE);

        if (_totalCritChanceBonus > 0f)
            _playerStats.AddStatModifier(new StatModifier(
                StatType.CritChance, _totalCritChanceBonus, ModifierType.Additive, source: STAT_SOURCE));

        if (_totalCritMultiplierBonus > 0f)
            _playerStats.AddStatModifier(new StatModifier(
                StatType.CritMultiplier, _totalCritMultiplierBonus, ModifierType.Additive, source: STAT_SOURCE));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private bool IsLocalPlayer()
    {
        // Dedicated Server не является локальным игроком
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && !NetworkManager.Singleton.IsHost) return false;
        if (GameModeManager.IsMode(GameMode.SinglePlayer)) return _playerStats != null;
        return _playerNetwork != null && _playerNetwork.IsOwner;
    }

    private void TryFindCamera()
    {
        var m = GetComponentInParent<PlayerMovement>();
        if (m != null && m.cam != null) { _cam = m.cam; return; }
        if (Camera.main != null) _cam = Camera.main.transform;
    }

    public float GetDamage() => _currentDamage;
    public float GetAttackCooldown() => _currentAttackCooldown;
    public float GetAttackRange() => _currentAttackRange;

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Vector3 center = _playerTransform != null ? _playerTransform.position : transform.position;
        Gizmos.color = new Color(0f, 1f, 1f, 0.4f);
        Gizmos.DrawWireSphere(center, _currentAttackRange);

        if (_cam != null && _currentAttackAngle < 360f)
        {
            Vector3 dir = _cam.forward; dir.y = 0; dir.Normalize();
            float half = _currentAttackAngle * 0.5f * Mathf.Deg2Rad;
            Vector3 left = new Vector3(
                dir.x * Mathf.Cos(-half) - dir.z * Mathf.Sin(-half), 0,
                dir.x * Mathf.Sin(-half) + dir.z * Mathf.Cos(-half));
            Vector3 right = new Vector3(
                dir.x * Mathf.Cos(half) - dir.z * Mathf.Sin(half), 0,
                dir.x * Mathf.Sin(half) + dir.z * Mathf.Cos(half));
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(center, left * _currentAttackRange);
            Gizmos.DrawRay(center, right * _currentAttackRange);
        }
    }
#endif
}