using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;

namespace Benchmark.FrifloEcs
{

public static class Schema
{
	private static bool _created;

	public static void Create()
	{
		if (_created)
			return;
		_created = true;

		if (RuntimeFeature.IsDynamicCodeSupported)
			return;

		var aot = new NativeAOT();
		aot.RegisterComponent<CompPosition>();
		aot.RegisterComponent<CompVelocity>();
		aot.RegisterComponent<CompSprite>();
		aot.RegisterComponent<CompUnit>();
		aot.RegisterComponent<CompData>();
		aot.RegisterComponent<CompHealth>();
		aot.RegisterComponent<CompDamage>();
		aot.RegisterComponent<AttackEntity>();
		aot.RegisterTag<TagSpawn>();
		aot.RegisterTag<TagDead>();
		aot.RegisterTag<TagNPC>();
		aot.RegisterTag<TagHero>();
		aot.RegisterTag<TagMonster>();
		aot.CreateSchema();
	}
}

}
