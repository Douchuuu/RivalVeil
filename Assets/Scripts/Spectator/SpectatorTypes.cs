using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Данные одного врага для трансляции спектатору.
///
/// INetworkSerializeByMemcpy — прямая побайтовая копия без аллокаций.
/// Только blittable типы: Vector3 (3 × float = 12 байт) + byte (1 байт).
/// Итого: 13 байт/враг. 700 врагов = ~9 KB/снапшот.
/// </summary>
public struct EnemyGhostData : INetworkSerializeByMemcpy
{
    public Vector3 Position;  // 12 bytes — мировая позиция врага
    public byte    IsMiniBoss; // 1 byte  — 0 = обычный враг, 1 = мини-босс
}
