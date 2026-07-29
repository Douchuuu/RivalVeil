using UnityEngine;
using Unity.Netcode;
using VContainer;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// AcidFlaskWeapon v3 — убран legacy IUpgradeable, исправлен баг круга, добавлен ProjectileSpeed.
///
/// Единственный путь апгрейда: IDynamicUpgradeable.ApplyDynamicUpgrade()
///
/// Поддерживаемые статы:
///   Damage          — % к базовому урону лужи
///   AttackSpeed     — % к скорости атаки + скорость залпа
///   Radius          — % к радиусу кислотной лужи
///   ProjectileCount — +N бутылок за залп
///   Duration        — % к длительности лужи
///   ProjectileSpeed — % к скорости полёта бутылки
///
/// ─── РЕЖИМЫ СТРЕЛЬБЫ ──────────────────────────────────────────────────────
///   1-3 снаряда: бьёт по ближайшему врагу (цель вычисляется в момент КАЖДОГО броска)
///   4+ снарядов: раскидывает по кругу (позиция пересчитывается в момент КАЖДОГО броска)
///
/// ─── ИСПРАВЛЕН БАГ ────────────────────────────────────────────────────────
///   БЫЛО: CalculateCircleTargets() вызывался один раз в начале корутины.
///         При движении игрока все последующие бутылки летели к точкам
///         вычисленным по НАЧАЛЬНОЙ позиции → все падали не там.
///
///   СТАЛО: позиция игрока и точки цели пересчитываются ПРИ КАЖДОМ броске
///          внутри цикла, после yield. Бутылки всегда летят от текущей позиции.
///
/// ─── ОПТИМИЗАЦИЯ ──────────────────────────────────────────────────────────
///   FindNearestEnemy использует EnemyPool.GetActiveEnemyHealths()
///   вместо FindObjectsByType — O(1) доступ, без просадок FPS.
/// </summary>
public class AcidFlaskWeapon : MonoBehaviour, IDynamicUpgradeable
{
    [Header("🧪 БАЗОВЫЕ ХАРАКТЕРИСТИКИ")]
    [SerializeField] private float baseDamage          = 8f;
    [SerializeField] private float baseAttackCooldown  = 3f;
    [SerializeField] private float basePuddleRadius    = 2.5f;
    [SerializeField] private float basePuddleDuration  = 5f;
    [SerializeField] private int   baseProjectileCount = 1;
    [SerializeField] private float baseFlightSpeed     = 12f;

    [Header("⏱ ЗАДЕРЖКА МЕЖДУ БУТЫЛКАМИ В ЗАЛПЕ")]
    [SerializeField] private float baseBurstDelay = 0.25f;

    [Header("📦 ПРЕФАБЫ")]
    [SerializeField] private GameObject flaskPrefab;
    [SerializeField] private GameObject puddlePrefab;

    // ── Текущие характеристики ────────────────────────────────────────────────
    private float _currentDamage;
    private float _currentAttackCooldown;
    private float _currentPuddleRadius;
    private float _currentPuddleDuration;
    private int   _currentProjectileCount;
    private float _currentFlightSpeed;
    private float _currentBurstDelay;

    // ── Накопленные бонусы ────────────────────────────────────────────────────
    private float _bonusDamagePct        = 0f;
    private float _bonusAttackSpeedPct   = 0f;
    private float _bonusRadiusPct        = 0f;
    private float _bonusDurationPct      = 0f;
    private float _bonusFlightSpeedPct   = 0f;
    private float _bonusExtraProjectiles = 0f;

    // ── Состояние ─────────────────────────────────────────────────────────────
    private float _attackTimer  = 0f;
    private bool  _isFiring     = false;
    private float _circleAngle  = 0f;

    private NetworkBehaviour _playerNetwork;
    private PlayerStats      _playerStats;
    private Transform        _playerTransform;
    private CombatCalculator _combatCalc;
    private EnemyPool        _enemyPool;

    // Радиус круга для режима 4+ снарядов
    private const float CIRCLE_RADIUS = 7f;

    // ─── INJECTION ────────────────────────────────────────────────────────────

    [Inject]
    public void Construct(CombatCalculator combatCalc, EnemyPool enemyPool)
    {
        _combatCalc = combatCalc;
        _enemyPool  = enemyPool;
        Debug.Log("[AcidFlaskWeapon] ✅ Зависимости инжектированы");
    }

    void Awake() => RecalcStats();

    void Start()
    {
        _playerNetwork   = GetComponentInParent<NetworkBehaviour>();
        _playerStats     = GetComponentInParent<PlayerStats>();
        _playerTransform = GetComponentInParent<Transform>();

        Debug.Log($"[AcidFlaskWeapon] Init: DMG={_currentDamage:F1}, CD={_currentAttackCooldown:F1}s, " +
                  $"R={_currentPuddleRadius:F1}, Dur={_currentPuddleDuration:F1}s, Proj={_currentProjectileCount}");
    }

    private bool IsLocalPlayer()
    {
        // Dedicated Server не является локальным игроком
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && !NetworkManager.Singleton.IsHost) return false;
        if (GameModeManager.IsMode(GameMode.SinglePlayer)) return _playerStats != null;
        return _playerNetwork != null && _playerNetwork.IsOwner;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПЕРЕСЧЁТ
    // ─────────────────────────────────────────────────────────────────────────

    void RecalcStats()
    {
        float atkMult          = 1f + _bonusAttackSpeedPct / 100f;
        _currentDamage         = baseDamage         * (1f + _bonusDamagePct       / 100f);
        _currentAttackCooldown = baseAttackCooldown  / atkMult;
        _currentBurstDelay     = baseBurstDelay      / atkMult;
        _currentPuddleRadius   = basePuddleRadius    * (1f + _bonusRadiusPct      / 100f);
        _currentPuddleDuration = basePuddleDuration  * (1f + _bonusDurationPct    / 100f);
        _currentFlightSpeed    = baseFlightSpeed      * (1f + _bonusFlightSpeedPct / 100f);
        _currentProjectileCount = Mathf.Max(1, baseProjectileCount + Mathf.FloorToInt(_bonusExtraProjectiles));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЦИКЛ АТАКИ
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!IsLocalPlayer()) return;
        if (_playerStats?.IsAttackBlocked == true) return;
        if (_isFiring) return;

        _attackTimer -= Time.deltaTime;
        if (_attackTimer <= 0f)
            StartCoroutine(FireBurst());
    }

    private IEnumerator FireBurst()
    {
        _isFiring = true;
        float attackSpeedMult   = _playerStats?.StatSheet?.GetStat(StatType.AttackSpeed) ?? 1f;
        float effectiveCooldown = _currentAttackCooldown / attackSpeedMult;
        float effectiveBurst    = _currentBurstDelay     / attackSpeedMult;

        if (_currentProjectileCount <= 3)
        {
            // Режим ближайшего врага: ищем цель в момент каждого броска
            for (int i = 0; i < _currentProjectileCount; i++)
            {
                Vector3 origin      = GetPlayerPosition();
                EnemyHealth nearest = FindNearestEnemy(origin);
                Vector3 target      = nearest != null
                    ? nearest.transform.position
                    : origin + (_playerTransform != null ? _playerTransform.forward : Vector3.forward) * 8f;

                ThrowFlask(target);

                if (i < _currentProjectileCount - 1)
                    yield return new WaitForSeconds(effectiveBurst);
            }
        }
        else
        {
            // Режим круга: позиция и точки круга пересчитываются в момент каждого броска.
            //
            // ФИКС: ранее CalculateCircleTargets() вызывался ОДИН РАЗ до цикла,
            // фиксируя все точки по начальной позиции. При движении игрока
            // последующие бутылки летели к устаревшим точкам.
            // Теперь каждая точка вычисляется непосредственно перед ThrowFlask.
            float angleStep = 360f / _currentProjectileCount;

            for (int i = 0; i < _currentProjectileCount; i++)
            {
                // Текущая позиция игрока на момент этого конкретного броска
                Vector3 origin = GetPlayerPosition();
                float   angle  = (_circleAngle + angleStep * i) * Mathf.Deg2Rad;
                Vector3 target = origin + new Vector3(
                    Mathf.Sin(angle) * CIRCLE_RADIUS,
                    0f,
                    Mathf.Cos(angle) * CIRCLE_RADIUS);

                ThrowFlask(target);

                if (i < _currentProjectileCount - 1)
                    yield return new WaitForSeconds(effectiveBurst);
            }

            // Смещаем угол для следующего залпа — небольшой поворот для красоты
            _circleAngle = (_circleAngle + angleStep * 0.5f) % 360f;
        }

        _attackTimer = effectiveCooldown;
        _isFiring    = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВСПОМОГАТЕЛЬНЫЕ
    // ─────────────────────────────────────────────────────────────────────────

    private Vector3 GetPlayerPosition()
    {
        return _playerTransform != null ? _playerTransform.position : transform.position;
    }

    /// <summary>
    /// Ищет ближайшего врага через EnemyPool (O(1) доступ к кэшу).
    /// Вместо FindObjectsByType — без просадок FPS при 500+ врагах.
    /// </summary>
    private EnemyHealth FindNearestEnemy(Vector3 origin)
    {
        IReadOnlyList<EnemyHealth> allActive = _enemyPool != null
            ? _enemyPool.GetActiveEnemyHealths()
            : (IReadOnlyList<EnemyHealth>)System.Array.Empty<EnemyHealth>();

        EnemyHealth nearest  = null;
        float       bestDist = float.MaxValue;

        foreach (var e in allActive)
        {
            if (e == null || !e.gameObject.activeInHierarchy) continue;
            float d = Vector3.Distance(origin, e.transform.position);
            if (d < bestDist) { bestDist = d; nearest = e; }
        }

        return nearest;
    }

    private void ThrowFlask(Vector3 targetPos)
    {
        if (flaskPrefab == null)  { Debug.LogError("[AcidFlaskWeapon] flaskPrefab не назначен!");  return; }
        if (puddlePrefab == null) { Debug.LogError("[AcidFlaskWeapon] puddlePrefab не назначен!"); return; }

        // Глобальные множители персонажа из StatSheet:
        //   ProjectileSpeedMultiplier → скорость полёта бутылки
        //   AttackRadiusMultiplier    → радиус лужи
        //   DurationMultiplier        → длительность лужи
        float charProjSpeedMult = _playerStats?.StatSheet?.GetStat(StatType.ProjectileSpeedMultiplier) ?? 1f;
        float charRadiusMult    = _playerStats?.StatSheet?.GetStat(StatType.AttackRadiusMultiplier)    ?? 1f;
        float charDurMult       = _playerStats?.StatSheet?.GetStat(StatType.DurationMultiplier)        ?? 1f;

        float finalSpeed    = _currentFlightSpeed    * charProjSpeedMult;
        float finalRadius   = _currentPuddleRadius   * charRadiusMult;
        float finalDuration = _currentPuddleDuration * charDurMult;

        Vector3    spawnPos = transform.position + Vector3.up * 0.5f;
        GameObject flaskObj = Instantiate(flaskPrefab, spawnPos, Quaternion.identity);
        AcidFlask  flask    = flaskObj.GetComponent<AcidFlask>();

        if (flask == null)
        {
            Debug.LogError("[AcidFlaskWeapon] AcidFlask на префабе не найден!");
            Destroy(flaskObj);
            return;
        }

        flask.Init(targetPos, finalSpeed, _currentDamage,
                   finalRadius, finalDuration,
                   _playerStats, _combatCalc, puddlePrefab);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDynamicUpgradeable
    // ─────────────────────────────────────────────────────────────────────────

    public float? GetCurrentStatValue(StatBonusType stat)
    {
        switch (stat)
        {
            case StatBonusType.Damage:          return _currentDamage;
            case StatBonusType.AttackSpeed:     return _currentAttackCooldown;
            case StatBonusType.Radius:          return _currentPuddleRadius;
            case StatBonusType.ProjectileCount: return _currentProjectileCount;
            case StatBonusType.Duration:        return _currentPuddleDuration;
            case StatBonusType.ProjectileSpeed: return _currentFlightSpeed;
            default:                            return null;
        }
    }

    public StatBonusType[] GetAvailableStats() => new[]
    {
        StatBonusType.Damage,
        StatBonusType.AttackSpeed,
        StatBonusType.Radius,
        StatBonusType.ProjectileCount,
        StatBonusType.Duration,
        StatBonusType.ProjectileSpeed,
    };

    public void ApplyDynamicUpgrade(WeaponUpgradeOffer offer)
    {
        Debug.Log($"⬆️ [AcidFlaskWeapon] {offer.Rarity} — ДО: DMG={_currentDamage:F1}, R={_currentPuddleRadius:F1}");

        foreach (var bonus in offer.Bonuses)
        {
            switch (bonus.type)
            {
                case StatBonusType.Damage:
                    _bonusDamagePct        += bonus.value; break;
                case StatBonusType.AttackSpeed:
                    _bonusAttackSpeedPct   += bonus.value; break;
                case StatBonusType.Radius:
                    _bonusRadiusPct        += bonus.value; break;
                case StatBonusType.ProjectileCount:
                    _bonusExtraProjectiles += bonus.value; break;
                case StatBonusType.Duration:
                    _bonusDurationPct      += bonus.value; break;
                case StatBonusType.ProjectileSpeed:
                    _bonusFlightSpeedPct   += bonus.value; break;
            }
        }

        RecalcStats();
        Debug.Log($"⬆️ [AcidFlaskWeapon] Готово — DMG={_currentDamage:F1}, R={_currentPuddleRadius:F1}, " +
                  $"Dur={_currentPuddleDuration:F1}, Speed={_currentFlightSpeed:F1}");
    }

    // ─────────────────────────────────────────────────────────────────────────

    public float GetDamage()          => _currentDamage;
    public float GetAttackCooldown()  => _currentAttackCooldown;
    public float GetPuddleRadius()    => _currentPuddleRadius;
    public float GetPuddleDuration()  => _currentPuddleDuration;
    public int   GetProjectileCount() => _currentProjectileCount;
    public float GetFlightSpeed()     => _currentFlightSpeed;

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (_playerTransform == null) return;
        Vector3 origin = _playerTransform.position;

        if (_currentProjectileCount >= 4)
        {
            float angleStep = 360f / _currentProjectileCount;
            Gizmos.color = new Color(0.2f, 0.9f, 0.1f, 0.6f);
            for (int i = 0; i < _currentProjectileCount; i++)
            {
                float   angle  = (_circleAngle + angleStep * i) * Mathf.Deg2Rad;
                Vector3 target = origin + new Vector3(
                    Mathf.Sin(angle) * CIRCLE_RADIUS, 0f, Mathf.Cos(angle) * CIRCLE_RADIUS);
                Gizmos.DrawLine(origin, target);
                Gizmos.DrawWireSphere(target, _currentPuddleRadius);
            }
        }
        else
        {
            EnemyHealth nearest = FindNearestEnemy(origin);
            if (nearest != null)
            {
                Gizmos.color = new Color(0.2f, 0.9f, 0.1f, 0.8f);
                Gizmos.DrawLine(origin, nearest.transform.position);
                Gizmos.DrawWireSphere(nearest.transform.position, _currentPuddleRadius);
            }
        }
    }
#endif
}
/*using UnityEngine;
using Unity.Netcode;
using VContainer;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// AcidFlaskWeapon v3 — убран legacy IUpgradeable, исправлен баг круга, добавлен ProjectileSpeed.
///
/// Единственный путь апгрейда: IDynamicUpgradeable.ApplyDynamicUpgrade()
///
/// Поддерживаемые статы:
///   Damage          — % к базовому урону лужи
///   AttackSpeed     — % к скорости атаки + скорость залпа
///   Radius          — % к радиусу кислотной лужи
///   ProjectileCount — +N бутылок за залп
///   Duration        — % к длительности лужи
///   ProjectileSpeed — % к скорости полёта бутылки
///
/// ─── РЕЖИМЫ СТРЕЛЬБЫ ──────────────────────────────────────────────────────
///   1-3 снаряда: бьёт по ближайшему врагу (цель вычисляется в момент КАЖДОГО броска)
///   4+ снарядов: раскидывает по кругу (позиция пересчитывается в момент КАЖДОГО броска)
///
/// ─── ИСПРАВЛЕН БАГ ────────────────────────────────────────────────────────
///   БЫЛО: CalculateCircleTargets() вызывался один раз в начале корутины.
///         При движении игрока все последующие бутылки летели к точкам
///         вычисленным по НАЧАЛЬНОЙ позиции → все падали не там.
///
///   СТАЛО: позиция игрока и точки цели пересчитываются ПРИ КАЖДОМ броске
///          внутри цикла, после yield. Бутылки всегда летят от текущей позиции.
///
/// ─── ОПТИМИЗАЦИЯ ──────────────────────────────────────────────────────────
///   FindNearestEnemy использует EnemyPool.GetActiveEnemyHealths()
///   вместо FindObjectsByType — O(1) доступ, без просадок FPS.
/// </summary>
public class AcidFlaskWeapon : MonoBehaviour, IDynamicUpgradeable
{
    [Header("🧪 БАЗОВЫЕ ХАРАКТЕРИСТИКИ")]
    [SerializeField] private float baseDamage          = 8f;
    [SerializeField] private float baseAttackCooldown  = 3f;
    [SerializeField] private float basePuddleRadius    = 2.5f;
    [SerializeField] private float basePuddleDuration  = 5f;
    [SerializeField] private int   baseProjectileCount = 1;
    [SerializeField] private float baseFlightSpeed     = 12f;

    [Header("⏱ ЗАДЕРЖКА МЕЖДУ БУТЫЛКАМИ В ЗАЛПЕ")]
    [SerializeField] private float baseBurstDelay = 0.25f;

    [Header("📦 ПРЕФАБЫ")]
    [SerializeField] private GameObject flaskPrefab;
    [SerializeField] private GameObject puddlePrefab;

    // ── Текущие характеристики ────────────────────────────────────────────────
    private float _currentDamage;
    private float _currentAttackCooldown;
    private float _currentPuddleRadius;
    private float _currentPuddleDuration;
    private int   _currentProjectileCount;
    private float _currentFlightSpeed;
    private float _currentBurstDelay;

    // ── Накопленные бонусы ────────────────────────────────────────────────────
    private float _bonusDamagePct        = 0f;
    private float _bonusAttackSpeedPct   = 0f;
    private float _bonusRadiusPct        = 0f;
    private float _bonusDurationPct      = 0f;
    private float _bonusFlightSpeedPct   = 0f;
    private float _bonusExtraProjectiles = 0f;

    // ── Состояние ─────────────────────────────────────────────────────────────
    private float _attackTimer  = 0f;
    private bool  _isFiring     = false;
    private float _circleAngle  = 0f;

    private NetworkBehaviour _playerNetwork;
    private PlayerStats      _playerStats;
    private Transform        _playerTransform;
    private CombatCalculator _combatCalc;
    private EnemyPool        _enemyPool;

    // Радиус круга для режима 4+ снарядов
    private const float CIRCLE_RADIUS = 7f;

    // ─── INJECTION ────────────────────────────────────────────────────────────

    [Inject]
    public void Construct(CombatCalculator combatCalc, EnemyPool enemyPool)
    {
        _combatCalc = combatCalc;
        _enemyPool  = enemyPool;
        Debug.Log("[AcidFlaskWeapon] ✅ Зависимости инжектированы");
    }

    void Awake() => RecalcStats();

    void Start()
    {
        _playerNetwork   = GetComponentInParent<NetworkBehaviour>();
        _playerStats     = GetComponentInParent<PlayerStats>();
        _playerTransform = GetComponentInParent<Transform>();

        Debug.Log($"[AcidFlaskWeapon] Init: DMG={_currentDamage:F1}, CD={_currentAttackCooldown:F1}s, " +
                  $"R={_currentPuddleRadius:F1}, Dur={_currentPuddleDuration:F1}s, Proj={_currentProjectileCount}");
    }

    private bool IsLocalPlayer()
    {
        if (GameModeManager.IsMode(GameMode.SinglePlayer)) return _playerStats != null;
        return _playerNetwork != null && _playerNetwork.IsOwner;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПЕРЕСЧЁТ
    // ─────────────────────────────────────────────────────────────────────────

    void RecalcStats()
    {
        float atkMult          = 1f + _bonusAttackSpeedPct / 100f;
        _currentDamage         = baseDamage         * (1f + _bonusDamagePct       / 100f);
        _currentAttackCooldown = baseAttackCooldown  / atkMult;
        _currentBurstDelay     = baseBurstDelay      / atkMult;
        _currentPuddleRadius   = basePuddleRadius    * (1f + _bonusRadiusPct      / 100f);
        _currentPuddleDuration = basePuddleDuration  * (1f + _bonusDurationPct    / 100f);
        _currentFlightSpeed    = baseFlightSpeed      * (1f + _bonusFlightSpeedPct / 100f);
        _currentProjectileCount = Mathf.Max(1, baseProjectileCount + Mathf.FloorToInt(_bonusExtraProjectiles));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЦИКЛ АТАКИ
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!IsLocalPlayer()) return;
        if (_playerStats?.IsAttackBlocked == true) return;
        if (_isFiring) return;

        _attackTimer -= Time.deltaTime;
        if (_attackTimer <= 0f)
            StartCoroutine(FireBurst());
    }

    private IEnumerator FireBurst()
    {
        _isFiring = true;
        float attackSpeedMult   = _playerStats?.StatSheet?.GetStat(StatType.AttackSpeed) ?? 1f;
        float effectiveCooldown = _currentAttackCooldown / attackSpeedMult;
        float effectiveBurst    = _currentBurstDelay     / attackSpeedMult;

        if (_currentProjectileCount <= 3)
        {
            // Режим ближайшего врага: ищем цель в момент каждого броска
            for (int i = 0; i < _currentProjectileCount; i++)
            {
                Vector3 origin      = GetPlayerPosition();
                EnemyHealth nearest = FindNearestEnemy(origin);
                Vector3 target      = nearest != null
                    ? nearest.transform.position
                    : origin + (_playerTransform != null ? _playerTransform.forward : Vector3.forward) * 8f;

                ThrowFlask(target);

                if (i < _currentProjectileCount - 1)
                    yield return new WaitForSeconds(effectiveBurst);
            }
        }
        else
        {
            // Режим круга: позиция и точки круга пересчитываются в момент каждого броска.
            //
            // ФИКС: ранее CalculateCircleTargets() вызывался ОДИН РАЗ до цикла,
            // фиксируя все точки по начальной позиции. При движении игрока
            // последующие бутылки летели к устаревшим точкам.
            // Теперь каждая точка вычисляется непосредственно перед ThrowFlask.
            float angleStep = 360f / _currentProjectileCount;

            for (int i = 0; i < _currentProjectileCount; i++)
            {
                // Текущая позиция игрока на момент этого конкретного броска
                Vector3 origin = GetPlayerPosition();
                float   angle  = (_circleAngle + angleStep * i) * Mathf.Deg2Rad;
                Vector3 target = origin + new Vector3(
                    Mathf.Sin(angle) * CIRCLE_RADIUS,
                    0f,
                    Mathf.Cos(angle) * CIRCLE_RADIUS);

                ThrowFlask(target);

                if (i < _currentProjectileCount - 1)
                    yield return new WaitForSeconds(effectiveBurst);
            }

            // Смещаем угол для следующего залпа — небольшой поворот для красоты
            _circleAngle = (_circleAngle + angleStep * 0.5f) % 360f;
        }

        _attackTimer = effectiveCooldown;
        _isFiring    = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВСПОМОГАТЕЛЬНЫЕ
    // ─────────────────────────────────────────────────────────────────────────

    private Vector3 GetPlayerPosition()
    {
        return _playerTransform != null ? _playerTransform.position : transform.position;
    }

    /// <summary>
    /// Ищет ближайшего врага через EnemyPool (O(1) доступ к кэшу).
    /// Вместо FindObjectsByType — без просадок FPS при 500+ врагах.
    /// </summary>
    private EnemyHealth FindNearestEnemy(Vector3 origin)
    {
        IReadOnlyList<EnemyHealth> allActive = _enemyPool != null
            ? _enemyPool.GetActiveEnemyHealths()
            : (IReadOnlyList<EnemyHealth>)System.Array.Empty<EnemyHealth>();

        EnemyHealth nearest  = null;
        float       bestDist = float.MaxValue;

        foreach (var e in allActive)
        {
            if (e == null || !e.gameObject.activeInHierarchy) continue;
            float d = Vector3.Distance(origin, e.transform.position);
            if (d < bestDist) { bestDist = d; nearest = e; }
        }

        return nearest;
    }

    private void ThrowFlask(Vector3 targetPos)
    {
        if (flaskPrefab == null)  { Debug.LogError("[AcidFlaskWeapon] flaskPrefab не назначен!");  return; }
        if (puddlePrefab == null) { Debug.LogError("[AcidFlaskWeapon] puddlePrefab не назначен!"); return; }

        // Глобальные множители персонажа из StatSheet:
        //   ProjectileSpeedMultiplier → скорость полёта бутылки
        //   AttackRadiusMultiplier    → радиус лужи
        //   DurationMultiplier        → длительность лужи
        float charProjSpeedMult = _playerStats?.StatSheet?.GetStat(StatType.ProjectileSpeedMultiplier) ?? 1f;
        float charRadiusMult    = _playerStats?.StatSheet?.GetStat(StatType.AttackRadiusMultiplier)    ?? 1f;
        float charDurMult       = _playerStats?.StatSheet?.GetStat(StatType.DurationMultiplier)        ?? 1f;

        float finalSpeed    = _currentFlightSpeed    * charProjSpeedMult;
        float finalRadius   = _currentPuddleRadius   * charRadiusMult;
        float finalDuration = _currentPuddleDuration * charDurMult;

        Vector3    spawnPos = transform.position + Vector3.up * 0.5f;
        GameObject flaskObj = Instantiate(flaskPrefab, spawnPos, Quaternion.identity);
        AcidFlask  flask    = flaskObj.GetComponent<AcidFlask>();

        if (flask == null)
        {
            Debug.LogError("[AcidFlaskWeapon] AcidFlask на префабе не найден!");
            Destroy(flaskObj);
            return;
        }

        flask.Init(targetPos, finalSpeed, _currentDamage,
                   finalRadius, finalDuration,
                   _playerStats, _combatCalc, puddlePrefab);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDynamicUpgradeable
    // ─────────────────────────────────────────────────────────────────────────

    public float? GetCurrentStatValue(StatBonusType stat)
    {
        switch (stat)
        {
            case StatBonusType.Damage:          return _currentDamage;
            case StatBonusType.AttackSpeed:     return _currentAttackCooldown;
            case StatBonusType.Radius:          return _currentPuddleRadius;
            case StatBonusType.ProjectileCount: return _currentProjectileCount;
            case StatBonusType.Duration:        return _currentPuddleDuration;
            case StatBonusType.ProjectileSpeed: return _currentFlightSpeed;
            default:                            return null;
        }
    }

    public StatBonusType[] GetAvailableStats() => new[]
    {
        StatBonusType.Damage,
        StatBonusType.AttackSpeed,
        StatBonusType.Radius,
        StatBonusType.ProjectileCount,
        StatBonusType.Duration,
        StatBonusType.ProjectileSpeed,
    };

    public void ApplyDynamicUpgrade(WeaponUpgradeOffer offer)
    {
        Debug.Log($"⬆️ [AcidFlaskWeapon] {offer.Rarity} — ДО: DMG={_currentDamage:F1}, R={_currentPuddleRadius:F1}");

        foreach (var bonus in offer.Bonuses)
        {
            switch (bonus.type)
            {
                case StatBonusType.Damage:
                    _bonusDamagePct        += bonus.value; break;
                case StatBonusType.AttackSpeed:
                    _bonusAttackSpeedPct   += bonus.value; break;
                case StatBonusType.Radius:
                    _bonusRadiusPct        += bonus.value; break;
                case StatBonusType.ProjectileCount:
                    _bonusExtraProjectiles += bonus.value; break;
                case StatBonusType.Duration:
                    _bonusDurationPct      += bonus.value; break;
                case StatBonusType.ProjectileSpeed:
                    _bonusFlightSpeedPct   += bonus.value; break;
            }
        }

        RecalcStats();
        Debug.Log($"⬆️ [AcidFlaskWeapon] Готово — DMG={_currentDamage:F1}, R={_currentPuddleRadius:F1}, " +
                  $"Dur={_currentPuddleDuration:F1}, Speed={_currentFlightSpeed:F1}");
    }

    // ─────────────────────────────────────────────────────────────────────────

    public float GetDamage()          => _currentDamage;
    public float GetAttackCooldown()  => _currentAttackCooldown;
    public float GetPuddleRadius()    => _currentPuddleRadius;
    public float GetPuddleDuration()  => _currentPuddleDuration;
    public int   GetProjectileCount() => _currentProjectileCount;
    public float GetFlightSpeed()     => _currentFlightSpeed;

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (_playerTransform == null) return;
        Vector3 origin = _playerTransform.position;

        if (_currentProjectileCount >= 4)
        {
            float angleStep = 360f / _currentProjectileCount;
            Gizmos.color = new Color(0.2f, 0.9f, 0.1f, 0.6f);
            for (int i = 0; i < _currentProjectileCount; i++)
            {
                float   angle  = (_circleAngle + angleStep * i) * Mathf.Deg2Rad;
                Vector3 target = origin + new Vector3(
                    Mathf.Sin(angle) * CIRCLE_RADIUS, 0f, Mathf.Cos(angle) * CIRCLE_RADIUS);
                Gizmos.DrawLine(origin, target);
                Gizmos.DrawWireSphere(target, _currentPuddleRadius);
            }
        }
        else
        {
            EnemyHealth nearest = FindNearestEnemy(origin);
            if (nearest != null)
            {
                Gizmos.color = new Color(0.2f, 0.9f, 0.1f, 0.8f);
                Gizmos.DrawLine(origin, nearest.transform.position);
                Gizmos.DrawWireSphere(nearest.transform.position, _currentPuddleRadius);
            }
        }
    }
#endif
}
*/