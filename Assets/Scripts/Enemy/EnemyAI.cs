using UnityEngine;
using VContainer;

/// <summary>
/// EnemyAI — обычный моб. Наследует BaseEnemyAI.
///
/// Реализует OnAttackPlayer() с логикой:
///   - Stomp (урон × stompDamageMultiplier при прыжке сверху)
///   - Knockback игрока (ApplyKnockback)
///   - Self-knockback + стан врага
/// </summary>
public class EnemyAI : BaseEnemyAI
{
    [Header("Knockback при атаке")]
    public float selfKnockbackForce = 5f;

    [Header("Урон при прыжке сверху")]
    public float stompDamageMultiplier = 1.5f;
    public float stompKnockbackForce   = 10f;

    // ─────────────────────────────────────────────────────────────────────────
    // АТАКА
    // ─────────────────────────────────────────────────────────────────────────

    protected override void OnAttackPlayer(Collision collision)
    {
        bool playerIsOnTop = IsPlayerOnTop(collision);

        PlayerStats    stats    = collision.gameObject.GetComponent<PlayerStats>();
        PlayerMovement movement = collision.gameObject.GetComponent<PlayerMovement>();

        if (stats != null)
        {
            float dmg = playerIsOnTop ? damage * stompDamageMultiplier : damage;
            stats.TakeDamage(dmg, _enemyHealth);

            if (movement != null)
            {
                if (playerIsOnTop)
                {
                    Vector3 lateralDir = (collision.transform.position - transform.position);
                    lateralDir.y = 0f;
                    Vector3 stompDir = lateralDir.sqrMagnitude > 0.001f
                        ? (lateralDir.normalized * 0.4f + Vector3.up * 0.9f).normalized
                        : Vector3.up;
                    movement.ApplyKnockback(stompDir, stompKnockbackForce);
                }
                else
                {
                    Vector3 toPlayer = (collision.transform.position - transform.position);
                    toPlayer.y = 0f;
                    Vector3 knockDir = toPlayer.sqrMagnitude > 0.001f
                        ? toPlayer.normalized
                        : transform.forward;
                    movement.ApplyKnockback(knockDir);
                }
            }
        }

        // Self-knockback + стан
        if (_rb != null)
        {
            Vector3 toEnemy = (transform.position - collision.transform.position);
            toEnemy.y = 0f;
            Vector3 selfDir = toEnemy.sqrMagnitude > 0.001f
                ? toEnemy.normalized
                : -transform.forward;

            _rb.linearVelocity = Vector3.zero;
            _rb.AddForce(selfDir * selfKnockbackForce, ForceMode.Impulse);
        }

        Stun(attackCooldown);
    }

    private bool IsPlayerOnTop(Collision collision)
    {
        foreach (var contact in collision.contacts)
            if (contact.normal.y > 0.5f) return true;
        return false;
    }
}
