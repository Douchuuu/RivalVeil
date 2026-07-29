using UnityEngine;
using UnityEngine.UIElements;
using VContainer;

/// <summary>
/// MiniBossAI — мини-босс. Наследует BaseEnemyAI.
///
/// ═══════════════════════════════════════════════════════════════════
/// ТЕКУЩИЕ ОТЛИЧИЯ ОТ ОБЫЧНОГО МОБА
/// ═══════════════════════════════════════════════════════════════════
///
///   - В 2 раза больше модель (настраивается на дочернем визуальном объекте)
///   - Больше HP (настраивается в EnemyHealth на префабе)
///   - Больше урон и скорость (настраивается в Inspector: damage, speed)
///   - Усиленный knockback игрока
///
/// ═══════════════════════════════════════════════════════════════════
/// КУДА ДОБАВЛЯТЬ НОВЫЕ МЕХАНИКИ МИНИ-БОССА
/// ═══════════════════════════════════════════════════════════════════
///
///   Атака / спецспособности:
///     → Расширяй OnAttackPlayer() или добавляй корутины/таймеры в Update()
///
///   Фазы:
///     → Подпишись на EnemyHealth.OnHealthChanged, переключай фазу
///
///   Аура, дэш, прыжок, снаряды:
///     → Добавляй отдельные методы, вызывай из Update() по таймерам
///
///   Все движение (включая фазы с ускорением):
///     → Меняй speed через SetSpeed() — BatchSystem автоматически подхватит
/// </summary>
public class MiniBossAI : BaseEnemyAI
{
    [Header("Характеристики мини-босса")]
    public float knockbackForce = 15f;

    // ─── Сюда будут добавляться поля для будущих механик ─────────────────────
    // Например:
    //   [Header("Фазы")]
    //   public float phase2HpThreshold = 0.5f;
    //   public float phase2SpeedMultiplier = 1.5f;
    //
    //   [Header("Дэш")]
    //   public float dashForce = 20f;
    //   public float dashCooldown = 5f;
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // АТАКА
    // ─────────────────────────────────────────────────────────────────────────

    protected override void OnAttackPlayer(Collision collision)
    {
        PlayerStats stats = collision.gameObject.GetComponent<PlayerStats>();
        PlayerMovement movement = collision.gameObject.GetComponent<PlayerMovement>();

        if (stats != null)
        {
            stats.TakeDamage(damage, _enemyHealth);

            if (movement != null)
            {
                Vector3 toPlayer = (collision.transform.position - transform.position);
                toPlayer.y = 0f;
                Vector3 knockDir = toPlayer.sqrMagnitude > 0.001f
                    ? toPlayer.normalized
                    : transform.forward;
                movement.ApplyKnockback(knockDir, knockbackForce);
            }
        }

        // Self-knockback мини-босса
        if (_rb != null)
        {
            Vector3 toSelf = (transform.position - collision.transform.position);
            toSelf.y = 0f;
            if (toSelf.sqrMagnitude > 0.001f)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.AddForce(toSelf.normalized * (knockbackForce * 0.3f), ForceMode.Impulse);
            }
        }

        Stun(attackCooldown);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РАСШИРЯЕМЫЕ ХУКИ (добавляй сюда будущую логику)
    // ─────────────────────────────────────────────────────────────────────────

    protected override void OnEnable()
    {
        base.OnEnable();
        // TODO: сброс фаз, аур, таймеров при спавне из пула
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        // TODO: отключение аур, эффектов при возврате в пул
    }

    public override void ResetToBase()
    {
        base.ResetToBase();
        // TODO: сброс специфичных для босса полей (фазы, кулдауны способностей)
    }
}