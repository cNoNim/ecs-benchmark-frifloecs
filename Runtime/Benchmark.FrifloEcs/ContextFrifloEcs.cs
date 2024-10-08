using System;
using System.Buffers;
using Benchmark.Core;
using Benchmark.Core.Algorithms;
using Benchmark.Core.Components;
using Benchmark.Core.Random;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using StableHash32 = Benchmark.Core.Hash.StableHash32;

namespace Benchmark.FrifloEcs
{

public class ContextFrifloEcs : ContextBase
{
	static ContextFrifloEcs() =>
		Schema.Create();

	private SystemRoot? _root;

	public ContextFrifloEcs()
		: base("Friflo Ecs") {}

	protected override void DoSetup()
	{
		var store = new EntityStore();
		_root = new SystemRoot(store)
		{
			new SystemGroup("Spawn")
			{
				new SpawnSystem(),
				new RespawnSystem(),
				new KillSystem(),
			},
			new RenderSystem(Framebuffer),
			new StateSpriteSystem<TagSpawn>(SpriteMask.Spawn),
			new StateSpriteSystem<TagDead>(SpriteMask.Grave),
			new UnitSpriteSystem<TagNPC>(SpriteMask.NPC),
			new UnitSpriteSystem<TagHero>(SpriteMask.Hero),
			new UnitSpriteSystem<TagMonster>(SpriteMask.Monster),
			new DamageSystem(),
			new AttackSystem(),
			new MovementSystem(),
			new UpdateVelocitySystem(),
			new UpdateDataSystem(),
		};

		store.EnsureCapacity(EntityCount);
		for (var i = 0; i < EntityCount; i++)
			store.CreateEntity<CompData, CompUnit>(
				default,
				new Unit
				{
					Id   = (uint) i,
					Seed = (uint) i,
				},
				Tags.Get<TagSpawn>());
	}

	protected override void DoRun(int tick) =>
		_root?.Update(default);

	protected override void DoCleanup() =>
		_root = null;

	private class SpawnSystem : QuerySystem<CompUnit, CompData>
	{
		private readonly EntityBatch _heroBatch;
		private readonly EntityBatch _monsterBatch;
		private readonly EntityBatch _npcBatch;

		public SpawnSystem()
		{
			Filter.AllTags(Tags.Get<TagSpawn>());
			_npcBatch = new EntityBatch().AddTag<TagNPC>()
										 .RemoveTag<TagSpawn>();
			_heroBatch = new EntityBatch().AddTag<TagHero>()
										  .RemoveTag<TagSpawn>();
			_monsterBatch = new EntityBatch().AddTag<TagMonster>()
											 .RemoveTag<TagSpawn>();
		}

		protected override void OnUpdate()
		{
			foreach (var entity in Query.Entities)
			{
				EntityBatch batch;
				switch (SpawnUnit(
							in entity.GetComponent<CompData>()
									 .V,
							ref entity.GetComponent<CompUnit>()
									  .V,
							out var health,
							out var damage,
							out var sprite,
							out var position,
							out var velocity))
				{
				case UnitType.NPC:
					batch = _npcBatch;
					break;
				case UnitType.Hero:
					batch = _heroBatch;
					break;
				case UnitType.Monster:
					batch = _monsterBatch;
					break;
				default:
					throw new ArgumentOutOfRangeException();
				}

				batch.Add<CompHealth>(health);
				batch.Add<CompDamage>(damage);
				batch.Add<CompSprite>(sprite);
				batch.Add<CompPosition>(position);
				batch.Add<CompVelocity>(velocity);
				batch.ApplyTo(entity);
			}
		}
	}

	private class UpdateDataSystem : QuerySystem<CompData>
	{
		protected override void OnUpdate()
		{
			foreach (var (data, entities) in Query.Chunks)
			{
				for (int n = 0; n < entities.Length; n++)
				{
					UpdateDataSystemForEach(ref data[n].V);
				}
			}
		}
	}

	private class UpdateVelocitySystem : QuerySystem<CompVelocity, CompUnit, CompData, CompPosition>
	{
		public UpdateVelocitySystem() =>
			Filter.WithoutAnyTags(Tags.Get<TagDead>());

		protected override void OnUpdate()
		{
			foreach (var (velocity, unit, data, position, entities) in Query.Chunks)
			{
				for (int n = 0; n < entities.Length; n++)
				{
					UpdateVelocitySystemForEach(
						ref velocity[n].V,
						ref unit[n].V,
						in data[n].V,
						in position[n].V);
				}
			}
		}
	}

	private class MovementSystem : QuerySystem<CompPosition, CompVelocity>
	{
		public MovementSystem() =>
			Filter.WithoutAnyTags(Tags.Get<TagDead>());

		protected override void OnUpdate()
		{
			foreach (var (position, velocity, entities) in Query.Chunks)
			{
				for (int n = 0; n < entities.Length; n++)
				{
					MovementSystemForEach(ref position[n].V, in velocity[n].V);
				}
			}
		}
	}

	private class AttackSystem : QuerySystem<CompUnit, CompData, CompDamage, CompPosition>
	{
		public AttackSystem() =>
			Filter.WithoutAnyTags(Tags.Get<TagSpawn, TagDead>());

		protected override void OnUpdate()
		{
			var count       = Query.Count;
			var keys        = ArrayPool<uint>.Shared.Rent(count);
			var indirection = ArrayPool<int>.Shared.Rent(count);
			var targets     = ArrayPool<Target<int>>.Shared.Rent(count);
			FillTargets(keys, targets);
			RadixSort.SortWithIndirection(keys, indirection, count);
			ArrayPool<uint>.Shared.Return(keys);
			CreateAttacks(indirection, targets.AsSpan(0, count));
			ArrayPool<int>.Shared.Return(indirection);
			ArrayPool<Target<int>>.Shared.Return(targets);
		}

		private void FillTargets(Span<uint> keys, Span<Target<int>> targets)
		{
			var i = 0;
			foreach (var (unitChunk, _, _, positionChunk, entities) in Query.Chunks)
			{
				var ids       = entities.Ids;
				var units     = unitChunk.Span;
				var positions = positionChunk.Span;
				for (var n = 0; n < ids.Length; n++)
				{
					var index = i++;
					keys[index]    = units[n].V.Id;
					targets[index] = new Target<int>(ids[n], positions[n].V);
				}
			}
		}

		private void CreateAttacks(ReadOnlySpan<int> indirection, ReadOnlySpan<Target<int>> targets)
		{
			var count = targets.Length;
			var store = Query.Store;
			foreach (var (unitChunk, dataChunk, damageChunk, positionChunk, entities) in Query.Chunks)
			{
				var ids       = entities.Ids;
				var units     = unitChunk.Span;
				var datas     = dataChunk.Span;
				var damages   = damageChunk.Span;
				var positions = positionChunk.Span;
				for (var n = 0; n < ids.Length; n++)
				{
					ref readonly var damage = ref damages[n].V;
					if (damage.Cooldown <= 0)
						continue;

					ref var          unit = ref units[n].V;
					ref readonly var data = ref datas[n].V;
					var              tick = data.Tick - unit.SpawnTick;
					if (tick % damage.Cooldown != 0)
						continue;

					ref readonly var position  = ref positions[n].V;
					var              generator = new RandomGenerator(unit.Seed);
					var              index     = generator.Random(ref unit.Counter, count);
					var              target    = targets[indirection[index]];
					store.CreateEntity(
						new AttackEntity
						{
							Target = store.GetEntityById(target.Entity),
							Damage = damage.Attack,
							Ticks  = Common.AttackTicks(position.V, target.Position),
						});
				}
			}
		}
	}

	private class DamageSystem : QuerySystem<AttackEntity>
	{
		public DamageSystem() =>
			Filter.WithoutAnyTags(Tags.Get<TagDead>());

		protected override void OnUpdate()
		{
			var commandBuffer = CommandBuffer;
			foreach (var (attackChunk, entities) in Query.Chunks)
			{
				var ids = entities.Ids;
				for (var n = 0; n < entities.Length; n++)
				{
					var     entity = ids[n];
					ref var attack = ref attackChunk[n];
					if (attack.Ticks-- > 0)
						continue;

					var targetData = attack.Target.Data;
					if (!targetData.IsNull
					 && !targetData.Tags.Has<TagDead>())
					{
						ref var health = ref targetData.Get<CompHealth>()
													   .V;
						ref readonly var damage = ref targetData.Get<CompDamage>()
																.V;
						ApplyDamageSequential(ref health, in damage, in attack);
					}
					commandBuffer.DeleteEntity(entity);
				}
			}
		}
	}

	private class KillSystem : QuerySystem<CompHealth, CompUnit, CompData>
	{
		public KillSystem() =>
			Filter.WithoutAnyTags(Tags.Get<TagDead>());

		protected override void OnUpdate()
		{
			var commandBuffer = CommandBuffer;
			foreach (var (healthChunk, unitChunk, dataChunk, entities) in Query.Chunks)
			{
				var ids = entities.Ids;
				for (int n = 0; n < entities.Length; n++)
				{
					if (healthChunk[n].V.Hp > 0)
						continue;
					commandBuffer.AddTag<TagDead>(ids[n]);
					unitChunk[n].V.RespawnTick = dataChunk[n].V.Tick + RespawnTicks;
				}
			}
		}
	}

	private class StateSpriteSystem<TTag> : QuerySystem<CompSprite>
		where TTag : struct, ITag
	{
		private readonly SpriteMask _sprite;

		public StateSpriteSystem(SpriteMask sprite)
		{
			_sprite = sprite;
			Filter.AllTags(Tags.Get<TTag>());
		}

		protected override void OnUpdate()
		{
			foreach (var (sprite, entities) in Query.Chunks)
			{
				for (int n = 0; n < entities.Length; n++)
				{
					sprite[n].V.Character = _sprite;
				}
			}
		}
	}

	private class UnitSpriteSystem<TTag> : QuerySystem<CompSprite>
		where TTag : struct, ITag
	{
		private readonly SpriteMask _sprite;

		public UnitSpriteSystem(SpriteMask sprite)
		{
			_sprite = sprite;
			Filter.AllTags(Tags.Get<TTag>())
				  .WithoutAnyTags(Tags.Get<TagSpawn, TagDead>());
		}

		protected override void OnUpdate()
		{
			foreach (var (sprite, entities) in Query.Chunks)
			{
				for (int n = 0; n < entities.Length; n++)
				{
					sprite[n].V.Character = _sprite;
				}
			}
		}
	}

	private class RenderSystem : QuerySystem<CompPosition, CompSprite, CompUnit, CompData>
	{
		private readonly Framebuffer _framebuffer;

		public RenderSystem(Framebuffer framebuffer) =>
			_framebuffer = framebuffer;

		protected override void OnUpdate()
		{
			var fb = _framebuffer;
			foreach (var (position, sprite, unit, data, entities) in Query.Chunks)
			{
				for (int n = 0; n < entities.Length; n++)
				{
					RenderSystemForEach(
						fb,
						in position[n].V,
						in sprite[n].V,
						in unit[n].V,
						in data[n].V);
				}
			}
		}
	}

	private class RespawnSystem : QuerySystem<CompUnit, CompData>
	{
		public RespawnSystem() =>
			Filter.AllTags(Tags.Get<TagDead>());

		protected override void OnUpdate() =>
			Query.ForEachEntity(
				(ref CompUnit unit, ref CompData data, Entity entity) =>
				{
					if (data.V.Tick < unit.V.RespawnTick)
						return;

					var commandBuffer = CommandBuffer;
					var newEntity     = commandBuffer.CreateEntity();
					commandBuffer.AddTag<TagSpawn>(newEntity);
					commandBuffer.AddComponent(newEntity, data);
					commandBuffer.AddComponent<CompUnit>(
						newEntity,
						new Unit
						{
							Id   = unit.V.Id | (uint) data.V.Tick << 16,
							Seed = StableHash32.Hash(unit.V.Seed, unit.V.Counter),
						});
					commandBuffer.DeleteEntity(entity.Id);
				});
	}
}

}
