using System.Runtime.CompilerServices;
using Benchmark.Core.Components;
using Friflo.Engine.ECS;
using Unity.Mathematics;
using Position = Benchmark.Core.Components.Position;

namespace Benchmark.FrifloEcs
{

public struct CompPosition : IComponent
{
	public Position V;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator CompPosition(Position value) =>
		new() { V = value };
}

public struct CompVelocity : IComponent
{
	public Velocity V;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator CompVelocity(Velocity value) =>
		new() { V = value };
}

public struct CompSprite : IComponent
{
	public Sprite V;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator CompSprite(Sprite value) =>
		new() { V = value };
}

public struct CompUnit : IComponent
{
	public Unit V;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator CompUnit(Unit value) =>
		new() { V = value };
}

public struct CompData : IComponent
{
	public Data V;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator CompData(Data value) =>
		new() { V = value };
}

public struct CompHealth : IComponent
{
	public Health V;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator CompHealth(Health value) =>
		new() { V = value };
}

public struct CompDamage : IComponent
{
	public Damage V;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator CompDamage(Damage value) =>
		new() { V = value };
}

public struct AttackEntity : IComponent
{
	public Entity Target;
	public int    Damage;
	public int    Ticks;
}

public struct TargetEntity
{
	public Entity Entity;
	public float2 Position;

	public TargetEntity(Entity entity, Position position)
	{
		Entity   = entity;
		Position = position.V;
	}
}

}
