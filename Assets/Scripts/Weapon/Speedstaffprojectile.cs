using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════
/// SPEED STAFF PROJECTILE — снаряд Посоха Скорости
/// ══════════════════════════════════════════════════════════════════════
///
/// МЕХАНИКА:
///   Снаряд летит к назначенной цели с homing-трекингом.
///   При попадании: наносит урон и проверяет пронзание.
///
/// ПРОНЗАНИЕ (piercingCount):
///   0 → снаряд уничтожается после первого врага (1 цель)
///   1 → проходит сквозь 1 врага, уничтожается на 2-м (2 цели)
///   N → проходит сквозь N врагов, уничтожается на (N+1)-м
///
///   После прохождения сквозь врага — ищет новую ближайшую незатронутую цель.
///
/// ИНИЦИАЛИЗАЦИЯ:
///   Снаряд не использует [Inject] — данные передаются через Init().
///   Это позволяет создавать через Instantiate() без VContainer.
///
/// LIFETIME:
///   Если цель умерла — летит прямо. Через MAX_LIFETIME самоуничтожается.
/// </summary>
public class SpeedStaffProjectile : MonoBehaviour
{
    // ─── RUNTIME-ДАННЫЕ (задаются через Init) ────────────────────────────────
    private EnemyHealth _target;
    private float _damage;
    private float _speed;
    private int _piercingLeft;   // сколько врагов ещё можно пронзить
    private PlayerStats _owner;
    private CombatCalculator _combatCalc;

    // Список уже поражённых врагов — не бьём дважды
    private readonly HashSet<EnemyHealth> _hitEnemies = new HashSet<EnemyHealth>();

    // ─── КОНСТАНТЫ ───────────────────────────────────────────────────────────
    private const float MAX_LIFETIME = 8f;   // секунд до самоуничтожения
    private const float HOMING_STRENGTH = 12f;  // скорость поворота к цели
    private const float HIT_DISTANCE = 0.6f; // расстояние считается попаданием

    private float _lifetime = 0f;

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Вызывается из SpeedStaffWeapon сразу после Instantiate().
    ///
    /// piercing:
    ///   0 = обычный (поражает 1 цель)
    ///   1 = проходит сквозь 1 врага (поражает 2 цели)
    ///   N = проходит сквозь N врагов (поражает N+1 целей)
    /// </summary>
    public void Init(
        EnemyHealth target,
        float damage,
        float speed,
        int piercing,
        PlayerStats owner,
        CombatCalculator combatCalc)
    {
        _target = target;
        _damage = damage;
        _speed = speed;
        _piercingLeft = piercing;
        _owner = owner;
        _combatCalc = combatCalc;
        _lifetime = 0f;
        _hitEnemies.Clear();

        // Сразу смотрим в сторону цели
        if (target != null)
        {
            Vector3 dir = target.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
                transform.forward = dir.normalized;
        }

        Debug.Log($"[SpeedStaffProjectile] Инициализирован. Цель={target?.name ?? "null"}, " +
                  $"DMG={damage:F1}, Speed={speed:F1}, Piercing={piercing}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE — движение и проверка попадания
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        _lifetime += Time.deltaTime;

        if (_lifetime > MAX_LIFETIME)
        {
            Destroy(gameObject);
            return;
        }

        // Homing: поворачиваемся к цели
        if (_target != null && _target.gameObject.activeInHierarchy)
        {
            Vector3 dir = _target.transform.position - transform.position;
            dir.y = 0f;  // держим горизонтально

            float dist = dir.magnitude;

            if (dir.sqrMagnitude > 0.001f)
            {
                transform.forward = Vector3.Lerp(
                    transform.forward,
                    dir.normalized,
                    Time.deltaTime * HOMING_STRENGTH);
            }

            // Проверка попадания по дистанции (если нет коллайдера у врага)
            if (dist < HIT_DISTANCE)
                HandleHit(_target);
        }

        // Движение вперёд
        transform.position += transform.forward * _speed * Time.deltaTime;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // КОЛЛИЗИИ
    // ─────────────────────────────────────────────────────────────────────────

    void OnTriggerEnter(Collider other)
    {
        EnemyHealth enemy = other.GetComponent<EnemyHealth>();
        if (enemy == null) return;
        HandleHit(enemy);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЛОГИКА ПОПАДАНИЯ
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleHit(EnemyHealth enemy)
    {
        if (enemy == null) return;
        if (_hitEnemies.Contains(enemy)) return;

        _hitEnemies.Add(enemy);

        // Расчёт урона
        float finalDamage = CalculateDamage(enemy);
        enemy.TakeDamage(finalDamage, _owner);

        Debug.Log($"[SpeedStaffProjectile] Попал в {enemy.name}, DMG={finalDamage:F1}, " +
                  $"PiercingLeft={_piercingLeft}");

        if (_piercingLeft <= 0)
        {
            // Пронзаний не осталось — уничтожаемся
            Destroy(gameObject);
        }
        else
        {
            // Пронзаем: уменьшаем счётчик и продолжаем лететь по той же траектории.
            // _target обнуляем — homing отключается, снаряд летит прямо дальше.
            _piercingLeft--;
            _target = null;

            Debug.Log($"[SpeedStaffProjectile] Пронзил врага, PiercingLeft={_piercingLeft}. Летит по прямой дальше.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РАСЧЁТ УРОНА (использует CombatCalculator с критами игрока)
    // ─────────────────────────────────────────────────────────────────────────

    private float CalculateDamage(EnemyHealth enemy)
    {
        if (_combatCalc != null && _owner != null)
        {
            var result = _combatCalc.Calculate(_damage, _owner, enemy);
            return result.FinalDamage;
        }
        return Mathf.Max(1f, _damage);
    }


#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        // Визуализируем направление полёта
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(transform.position, transform.forward * 1.5f);

        // Визуализируем линию к цели
        if (_target != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(transform.position, _target.transform.position);
        }
    }
#endif
}