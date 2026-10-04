using LCReplay.Core;
internal static class TreeBreakTimingTests
{
    public static void Run()
    {
        var world = new WorldSnapshot { CaptureSetId = "map", Geometry = new() { new GeometrySnapshot {
            Id="tree", Name="tree_LOD0", Scale=Vec3.One, Rotation=Quat.Identity,
            Vertices=new float[]{-.1f,0,0,.1f,0,0,0,8,0} } } };
        ReplayEvent Hidden(string id="tree", string set="map") => new() {Time=96,Category="visual",Name="renderer",EntityId=id,Data=new(){["set"]=set,["visible"]="false"}};
        var sound = new ReplayEvent {Time=32,Category="sound",Name="play",Data=new(){["clip"]="BreakTree1",["x"]="0",["y"]="1",["z"]="0"}};
        var hidden=Hidden();
        void Check(bool ok){if(!ok)throw new Exception("Tree destruction timing regression");}
        var fixedEvents=ReplayTreeBreakTiming.Correct(world,new[]{sound,hidden}).ToArray();
        Check(fixedEvents[1].Time==32 && hidden.Time==96);
        Check(ReplayTreeBreakTiming.Correct(world,new[]{sound}).Count()==1);
        Check(ReplayTreeBreakTiming.Correct(world,new[]{sound,Hidden(set:"other")}).Last().Time==96);
        sound.Data["x"]="2";Check(ReplayTreeBreakTiming.Correct(world,new[]{sound,hidden}).Last().Time==96);
        sound.Data["x"]="0";sound.Data["anchor"]="ship";Check(ReplayTreeBreakTiming.Correct(world,new[]{sound,hidden}).Last().Time==96);
        sound.Data.Remove("anchor");sound.Data["clip"]="Explosion";Check(ReplayTreeBreakTiming.Correct(world,new[]{sound,hidden}).Last().Time==96);
        sound.Data["clip"]="BreakTree1";sound.Time=100;Check(ReplayTreeBreakTiming.Correct(world,new[]{sound,hidden}).Last().Time==96);
        sound.Time=32;world.Geometry[0].Name="door";Check(ReplayTreeBreakTiming.Correct(world,new[]{sound,hidden}).Last().Time==96);
    }
}
