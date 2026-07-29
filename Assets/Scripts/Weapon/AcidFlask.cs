using UnityEngine;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════
/// ACID FLASK — брошенная кислотная бутылка
/// ══════════════════════════════════════════════════════════════════════
///
/// Летит по параболической дуге к целевой точке.
/// При достижении цели (или земли) — создаёт AcidPuddle и уничтожается.
///
/// ТРАЕКТОРИЯ:
///   Простая симуляция дуги: двигаемся к targetPoint с Lerp по t [0..1].
///   По вертикали добавляем параболу: height * 4 * t * (1 - t).
///   Это даёт красивую дугу без физики/гравитации Unity.
///
/// ИНИЦИАЛИЗАЦИЯ через Init() — не использует [Inject].
/// Данные передаются напрямую из AcidFlaskWeapon.
/// </summary>
public class AcidFlask : MonoBehaviour
{
    // ─── НАСТРОЙКИ ────────────────────────────────────────────────────────────
    [Tooltip("Высота дуги броска")]
    [SerializeField] private float arcHeight = 3f;

    [Tooltip("Максимальное время полёта в секундах (защита от бесконечного лёта)")]
    [SerializeField] private float maxFlightTime = 3f;

    // ─── RUNTIME ДАННЫЕ ───────────────────────────────────────────────────────
    private Vector3  _startPos;
    private Vector3  _targetPos;
    private float    _flightDuration;   // сколько секунд лететь до цели
    private float    _t = 0f;           // прогресс [0..1]

    // Данные для создания лужи
    private float         _puddleDamage;
    private float         _puddleRadius;
    private float         _puddleDuration;
    private PlayerStats   _owner;
    private CombatCalculator _combatCalc;
    private GameObject    _puddlePrefab;

    private bool _initialized = false;

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Вызывается из AcidFlaskWeapon сразу после Instantiate().
    /// targetPos — точка куда летит бутылка (позиция врага или круговая точка).
    /// flightSpeed — скорость в у.е./с (дистанция / flightSpeed = время полёта).
    /// </summary>
    public void Init(
        Vector3       targetPos,
        float         flightSpeed,
        float         puddleDamage,
        float         puddleRadius,
        float         puddleDuration,
        PlayerStats   owner,
        CombatCalculator combatCalc,
        GameObject    puddlePrefab)
    {
        _startPos       = transform.position;
        _targetPos      = targetPos;
        _targetPos.y    = 0f;   // лужа всегда на земле

        float dist      = Vector3.Distance(_startPos, _targetPos);
        _flightDuration = Mathf.Max(0.1f, dist / Mathf.Max(1f, flightSpeed));
        _flightDuration = Mathf.Min(_flightDuration, maxFlightTime);

        _puddleDamage   = puddleDamage;
        _puddleRadius   = puddleRadius;
        _puddleDuration = puddleDuration;
        _owner          = owner;
        _combatCalc     = combatCalc;
        _puddlePrefab   = puddlePrefab;

        _t              = 0f;
        _initialized    = true;

        // Сразу смотрим в сторону цели
        Vector3 dir = _targetPos - _startPos;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.001f)
            transform.forward = dir.normalized;

        Debug.Log($"[AcidFlask] Инициализирована. Target={targetPos}, " +
                  $"Dist={dist:F1}, FlightTime={_flightDuration:F2}s");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE — движение по дуге
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!_initialized) return;

        _t += Time.deltaTime / _flightDuration;
        _t  = Mathf.Clamp01(_t);

        // Параболическая дуга
        Vector3 flatPos = Vector3.Lerp(_startPos, _targetPos, _t);
        float   yArc    = arcHeight * 4f * _t * (1f - _t);

        transform.position = new Vector3(flatPos.x, flatPos.y + yArc, flatPos.z);

        // Визуальный поворот — бутылка "смотрит" по направлению полёта
        if (_t < 0.99f)
        {
            Vector3 nextFlat = Vector3.Lerp(_startPos, _targetPos, _t + 0.01f);
            float   nextY    = arcHeight * 4f * (_t + 0.01f) * (1f - (_t + 0.01f));
            Vector3 nextPos  = new Vector3(nextFlat.x, nextFlat.y + nextY, nextFlat.z);
            Vector3 dir      = nextPos - transform.position;
            if (dir.sqrMagnitude > 0.0001f)
                transform.forward = dir.normalized;
        }

        // Достигли цели
        if (_t >= 1f)
            Land();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПРИЗЕМЛЕНИЕ
    // ─────────────────────────────────────────────────────────────────────────

    private void Land()
    {
        SpawnPuddle();
        Destroy(gameObject);
    }

    private void SpawnPuddle()
    {
        if (_puddlePrefab == null)
        {
            Debug.LogError("[AcidFlask] ❌ puddlePrefab не назначен!");
            return;
        }

        Vector3 puddlePos = new Vector3(_targetPos.x, 0.05f, _targetPos.z); // чуть над землёй
        GameObject puddleObj = Instantiate(_puddlePrefab, puddlePos, Quaternion.identity);
        AcidPuddle puddle = puddleObj.GetComponent<AcidPuddle>();

        if (puddle == null)
        {
            Debug.LogError("[AcidFlask] ❌ На puddlePrefab нет компонента AcidPuddle!");
            Destroy(puddleObj);
            return;
        }

        puddle.Init(_puddleDamage, _puddleRadius, _puddleDuration, _owner, _combatCalc);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (!_initialized) return;

        Gizmos.color = new Color(0.2f, 0.9f, 0.1f, 0.8f);

        // Рисуем дугу из точек
        int steps = 20;
        Vector3 prev = _startPos;
        for (int i = 1; i <= steps; i++)
        {
            float   t    = (float)i / steps;
            Vector3 flat = Vector3.Lerp(_startPos, _targetPos, t);
            float   y    = arcHeight * 4f * t * (1f - t);
            Vector3 curr = new Vector3(flat.x, flat.y + y, flat.z);
            Gizmos.DrawLine(prev, curr);
            prev = curr;
        }

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(_targetPos, 0.3f);
    }
#endif
}
