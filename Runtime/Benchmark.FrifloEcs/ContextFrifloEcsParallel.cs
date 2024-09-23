using System;
using System.Buffers;
using System.Threading;
using Benchmark.Core;
using Benchmark.Core.Algorithms;
using Benchmark.Core.Components;
using Benchmark.Core.Random;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using StableHash32 = Benchmark.Core.Hash.StableHash32;

namespace Benchmark.FrifloEcs
{

public class ContextFrifloEcsParallel : ContextBase
{
	static ContextFrifloEcsParallel() =>
		Schema.Create();

	private SystemRoot? _root;

	public ContextFrifloEcsParallel()
		: base("Friflo Ecs Parallel") {}

	protected override void DoSetup()
	{
		var store = new EntityStore
		{
			JobRunner = new ParallelJobRunner(4),
		};
		_root = new SystemRoot(store)
		{
			new SystemGroup("Spawn")
			{
				new SpawnSystem(),
				new RespawnSystem(),
				new KillSystem(),
			},
			new RenderSystem(Framebuffer.AsParallel()),
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
		private QueryJob<CompUnit, CompData>? _job;

		public SpawnSystem()
		{
			Filter.AllTags(Tags.Get<TagSpawn>());
		}

		protected override void OnUpdate()
		{
			if (_job == null)
			{
				var commandBuffer = CommandBuffer.Synced;
				_job = Query.ForEach(
					(units, data, entities) =>
					{
						for (var n = 0; n < entities.Length; n++)
						{
							var entity = entities[n];
							switch (SpawnUnit(
										in data[n].V,
										ref units[n].V,
										out var health,
										out var damage,
										out var sprite,
										out var position,
										out var velocity))
							{
							case UnitType.NPC:
								commandBuffer.AddTag<TagNPC>(entity);
								break;
							case UnitType.Hero:
								commandBuffer.AddTag<TagHero>(entity);
								break;
							case UnitType.Monster:
								commandBuffer.AddTag<TagMonster>(entity);
								break;
							default:
								throw new ArgumentOutOfRangeException();
							}

							commandBuffer.RemoveTag<TagSpawn>(entity);
							commandBuffer.AddComponent<CompHealth>(entity, health);
							commandBuffer.AddComponent<CompDamage>(entity, damage);
							commandBuffer.AddComponent<CompSprite>(entity, sprite);
							commandBuffer.AddComponent<CompPosition>(entity, position);
							commandBuffer.AddComponent<CompVelocity>(entity, velocity);
						}
					});
			}
			_job.RunParallel();
		}
	}

	private class RespawnSystem : QuerySystem<CompUnit, CompData>
	{
		private QueryJob<CompUnit, CompData>? _job;

		public RespawnSystem() =>
			Filter.AllTags(Tags.Get<TagDead>());

		protected override void OnUpdate()
		{
			if (_job == null)
			{
				var commandBuffer = CommandBuffer.Synced;
				_job = Query.ForEach(
					(units, datas, entities) =>
					{
						for (var n = 0; n < entities.Length; n++)
						{
							ref readonly var unit = ref units[n];
							ref readonly var data = ref datas[n];
							if (data.V.Tick < unit.V.RespawnTick)
								continue;

							var newEntity = commandBuffer.CreateEntity();
							commandBuffer.AddTag<TagSpawn>(newEntity);
							commandBuffer.AddComponent(newEntity, data);
							commandBuffer.AddComponent<CompUnit>(
								newEntity,
								new Unit
								{
									Id   = unit.V.Id | (uint) data.V.Tick << 16,
									Seed = StableHash32.Hash(unit.V.Seed, unit.V.Counter),
								});
							commandBuffer.DeleteEntity(entities[n]);
						}
					});
			}
			_job.RunParallel();
		}
	}

	private class KillSystem : QuerySystem<CompHealth, CompUnit, CompData>
	{
		private QueryJob<CompHealth, CompUnit, CompData>? _job;

		public KillSystem() =>
			Filter.WithoutAnyTags(Tags.Get<TagDead>());

		protected override void OnUpdate()
		{
			if (_job == null)
			{
				var commandBuffer = CommandBuffer.Synced;
				_job = Query.ForEach(
					(
						healths,
						units,
						data,
						entities) =>
					{
						for (var n = 0; n < entities.Length; n++)
						{
							if (healths[n].V.Hp > 0)
								continue;
							commandBuffer.AddTag<TagDead>(entities[n]);
							units[n].V.RespawnTick = data[n].V.Tick + RespawnTicks;
						}
					});
			}
			_job.RunParallel();
		}
	}

	private class RenderSystem : QuerySystem<CompPosition, CompSprite, CompUnit, CompData>
	{
		private readonly FramebufferParallel                                     _framebuffer;
		private          QueryJob<CompPosition, CompSprite, CompUnit, CompData>? _job;

		public RenderSystem(FramebufferParallel framebuffer)
		{
			_framebuffer = framebuffer;
		}

		protected override void OnUpdate()
		{
			_job ??= Query.ForEach(
				(
					positions,
					sprites,
					units,
					data,
					entities) =>
				{
					for (var n = 0; n < entities.Length; n++)
						RenderSystemForEach(
							_framebuffer,
							in positions[n].V,
							in sprites[n].V,
							in units[n].V,
							in data[n].V);
				});
			_job.RunParallel();
		}
	}

	private class StateSpriteSystem<TTag> : QuerySystem<CompSprite>
		where TTag : struct, ITag
	{
		private readonly SpriteMask            _sprite;
		private          QueryJob<CompSprite>? _job;

		public StateSpriteSystem(SpriteMask sprite)
		{
			Filter.AllTags(Tags.Get<TTag>());
			_sprite = sprite;
		}

		protected override void OnUpdate()
		{
			_job ??= Query.ForEach(
				(sprites, entities) =>
				{
					for (var n = 0; n < entities.Length; n++)
						sprites[n].V.Character = _sprite;
				});
			_job.RunParallel();
		}
	}

	private class UnitSpriteSystem<TTag> : QuerySystem<CompSprite>
		where TTag : struct, ITag
	{
		private readonly SpriteMask            _sprite;
		private          QueryJob<CompSprite>? _job;

		public UnitSpriteSystem(SpriteMask sprite)
		{
			_sprite = sprite;
			Filter.AllTags(Tags.Get<TTag>())
				  .WithoutAnyTags(Tags.Get<TagSpawn, TagDead>());
		}

		protected override void OnUpdate()
		{
			_job ??= Query.ForEach(
				(sprites, entities) =>
				{
					for (var n = 0; n < entities.Length; n++)
						sprites[n].V.Character = _sprite;
				});
			_job.RunParallel();
		}
	}

	private class DamageSystem : QuerySystem<AttackEntity>
	{
		private QueryJob<AttackEntity>? _job;

		public DamageSystem()
		{
			Filter.WithoutAnyTags(Tags.Get<TagDead>());
		}

		protected override void OnUpdate()
		{
			if (_job == null)
			{
				var commandBuffer = CommandBuffer.Synced;
				_job = Query.ForEach(
					(attacks, entities) =>
					{
						for (var n = 0; n < entities.Length; n++)
						{
							var     entity = entities[n];
							ref var attack = ref attacks[n];
							if (attack.Ticks-- > 0)
								continue;

							var targetData = attack.Target.Data;
							if (!targetData.IsNull
							 && !targetData.Tags.Has<TagDead>())
							{
								ref var health = ref targetData.Get<CompHealth>()
															   .V;
								ref var damage = ref targetData.Get<CompDamage>()
															   .V;
								ApplyDamageParallel(ref health, in damage, in attack);
							}
							commandBuffer.DeleteEntity(entity);
						}
					});
			}

			_job.RunParallel();
		}
	}

	private class AttackSystem : QuerySystem<CompUnit, CompData, CompDamage, CompPosition>
	{
		private QueryJob<CompUnit, CompData, CompDamage, CompPosition>? _fillJob;
		private QueryJob<CompUnit, CompData, CompDamage, CompPosition>? _createAttacksJob;
		private int                                                     _index;
		private uint[]?                                                 _keys;
		private Target<int>[]?                                          _targets;
		private int                                                     _count;
		private int[]?                                                  _indirection;

		public AttackSystem() =>
			Filter.WithoutAnyTags(Tags.Get<TagSpawn, TagDead>());

		protected override void OnUpdate()
		{
			if (_fillJob          == null
			 || _createAttacksJob == null)
			{
				var commandBuffer = CommandBuffer.Synced;
				_fillJob = Query.ForEach(
					(
						units,
						_,
						_,
						positions,
						entities) =>
					{
						for (var n = 0; n < entities.Length; n++)
						{
							var index = Interlocked.Increment(ref _index) - 1;
							_keys![index]    = units[n].V.Id;
							_targets![index] = new Target<int>(entities[n], positions[n].V);
						}
					});
				_createAttacksJob = Query.ForEach(
					(
						units,
						datas,
						damages,
						positions,
						entities) =>
					{
						var store   = Query.Store;
						var count   = _count;
						var targets = _targets.AsSpan(0, count);
						for (var n = 0; n < entities.Length; n++)
						{
							ref readonly var damage = ref damages[n].V;
							if (damage.Cooldown <= 0)
								continue;

							ref var          unit = ref units[n].V;
							ref readonly var data = ref datas[n].V;
							var              tick = data.Tick - unit.SpawnTick;
							if (tick % damage.Cooldown != 0)
								continue;

							ref readonly var position     = ref positions[n].V;
							var              generator    = new RandomGenerator(unit.Seed);
							var              index        = generator.Random(ref unit.Counter, count);
							var              target       = targets[_indirection![index]];
							var              attackEntity = commandBuffer.CreateEntity();
							commandBuffer.AddComponent(
								attackEntity,
								new AttackEntity
								{
									Target = store.GetEntityById(target.Entity),
									Damage = damage.Attack,
									Ticks  = Common.AttackTicks(position.V, target.Position),
								});
						}
					});
			}

			_index = 0;
			_count = Query.Count;
			try
			{
				_targets = ArrayPool<Target<int>>.Shared.Rent(_count);
				try
				{
					_indirection = ArrayPool<int>.Shared.Rent(_count);
					try
					{
						_keys = ArrayPool<uint>.Shared.Rent(_count);
						_fillJob.RunParallel();
						RadixSort.SortWithIndirection(_keys, _indirection, _count);
					}
					finally
					{
						ArrayPool<uint>.Shared.Return(_keys!);
						_keys = null;
					}

					_createAttacksJob.RunParallel();
				}
				finally
				{
					ArrayPool<int>.Shared.Return(_indirection!);
					_indirection = null;
				}
			}
			finally
			{
				ArrayPool<Target<int>>.Shared.Return(_targets!);
				_targets = null;
			}
		}
	}

	private class MovementSystem : QuerySystem<CompPosition, CompVelocity>
	{
		private QueryJob<CompPosition, CompVelocity>? _job;

		public MovementSystem() =>
			Filter.WithoutAnyTags(Tags.Get<TagDead>());

		protected override void OnUpdate()
		{
			_job ??= Query.ForEach(
				(positions, velocities, entities) =>
				{
					for (var n = 0; n < entities.Length; n++)
						MovementSystemForEach(ref positions[n].V, in velocities[n].V);
				});
			_job.RunParallel();
		}
	}

	private class UpdateVelocitySystem : QuerySystem<CompVelocity, CompUnit, CompData, CompPosition>
	{
		private QueryJob<CompVelocity, CompUnit, CompData, CompPosition>? _job;

		public UpdateVelocitySystem() =>
			Filter.WithoutAnyTags(Tags.Get<TagDead>());

		protected override void OnUpdate()
		{
			_job ??= Query.ForEach(
				(
					velocities,
					units,
					data,
					positions,
					entities) =>
				{
					for (var n = 0; n < entities.Length; n++)
						UpdateVelocitySystemForEach(
							ref velocities[n].V,
							ref units[n].V,
							in data[n].V,
							in positions[n].V);
				});
			_job.RunParallel();
		}
	}

	private class UpdateDataSystem : QuerySystem<CompData>
	{
		private QueryJob<CompData>? _job;

		protected override void OnUpdate()
		{
			_job ??= Query.ForEach(
				(data, entities) =>
				{
					for (var n = 0; n < entities.Length; n++)
						UpdateDataSystemForEach(ref data[n].V);
				});
			_job.RunParallel();
		}
	}
}

}
