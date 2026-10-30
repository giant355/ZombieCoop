using UnityEngine;

/// <summary>房主模拟和客户端预测共用的平面移动规则；暂不处理碰撞和重力。</summary>
public static class MovementSimulation
{
    public const float StepSeconds = 0.02f;
    public const float MoveSpeed = 4f;

    /// <summary>根据起点和输入计算一步后的新位置，不直接修改任何场景对象。</summary>
    public static Vector3 Step(Vector3 position, Vector2 moveInput)
    {
        Vector2 direction = Vector2.ClampMagnitude(moveInput, 1f);
        Vector3 velocity = new Vector3(direction.x, 0f, direction.y) * MoveSpeed;
        return position + velocity * StepSeconds;
    }
}