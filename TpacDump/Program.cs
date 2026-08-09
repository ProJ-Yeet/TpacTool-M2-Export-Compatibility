using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using TpacTool.Lib;

class Program
{
    static int Main(string[] args)
    {
        // 用法: TpacDump <AssetPackages目录> [动画名过滤]
        string dir = args.Length > 0 ? args[0]
            : @"D:\ProgramFiles\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\Native\AssetPackages";
        string filter = args.Length > 1 ? args[1] : "anim_";

        var manager = new AssetManager();
        Console.Error.WriteLine($"Loading {dir} ...");
        manager.Load(new DirectoryInfo(dir));
        Console.Error.WriteLine($"Loaded {manager.LoadedAssets.Count} assets from {manager.LoadedPackages.Count} packages");

        var clips = manager.LoadedAssets.OfType<AnimationClip>()
            .Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.UnknownClipName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.ClipSource1Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Console.Error.WriteLine($"Matching clips: {clips.Count}");

        // 模式3:批量导出所有骨骼动画的帧段表
        if (args.Length > 2 && args[2] == "all")
        {
            var allSks = manager.LoadedAssets.OfType<SkeletalAnimation>()
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var sk in allSks)
            {
                var refClips = manager.LoadedAssets.OfType<AnimationClip>()
                    .Where(c => c.Animation == sk.Guid)
                    .OrderBy(c => c.ClipSource1Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.ClipSource2Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (refClips.Count == 0) continue;   // 只输出有动作定义的
                // 读取骨骼动画总帧范围(rootPos 关键帧时间范围,用于 Unity 帧换算)
                float maxT = 0f;
                try
                {
                    var def = sk.Definition?.Data;
                    if (def != null && def.RootPositionFrames.Count > 0)
                        maxT = def.RootPositionFrames.Last().Key;
                }
                catch { /* 数据加载失败时用 0 */ }
                Console.WriteLine($"SK|{sk.Name}|dur={sk.Duration}|bones={sk.BoneNum}|maxT={maxT:F0}");
                foreach (var c in refClips)
                {
                    // 只取官方 clip(ClipSource1Name 为空 = 非生成变体,名字即动作名)
                    if (!string.IsNullOrEmpty(c.ClipSource1Name)) continue;
                    Console.WriteLine($"  CLIP|{c.Name}|s1={c.Source1:F0}|s2={c.Source2:F0}|dur={c.Duration:F3}|blendIn={c.BlendInPeriod:F3}|blendOut={c.BlendOutPeriod:F3}|priority={c.Priority}|combat={c.CombatParameterId}|step={c.StepPoints.X:F3}");
                }
            }
            return 0;
        }

        // 模式4:对比指定 clip 的 source1/source2 时刻全身姿势差异(验证循环首尾是否匹配)
        if (args.Length > 2 && args[2] == "cmp")
        {
            foreach (var clip in clips)
            {
                var sk = manager.GetAsset<SkeletalAnimation>(clip.Animation);
                if (sk == null) continue;
                var def = sk.Definition?.Data;
                if (def == null)
                {
                    Console.WriteLine($"{clip.Name}: no definition data");
                    continue;
                }
                // 采样区间:source1..source2(帧单位),blendIn/blendOut 分别从两端扣除后的纯循环段
                float t1 = clip.Source1, t2 = clip.Source2;
                float fps = 30f;
                float loopStart = clip.Source1 + clip.BlendInPeriod * fps;
                float loopEnd   = clip.Source2 - clip.BlendOutPeriod * fps;
                Console.WriteLine($"== {clip.Name} dur={clip.Duration:F3} s1={t1:F1} s2={t2:F1} blendIn={clip.BlendInPeriod:F3}(={clip.BlendInPeriod*fps:F1}帧) blendOut={clip.BlendOutPeriod:F3}(={clip.BlendOutPeriod*fps:F1}帧)");
                Console.WriteLine($"   循环段建议: [{loopStart:F1} .. {loopEnd:F1}] = {(loopEnd-loopStart):F1}帧@30fps = {(loopEnd-loopStart)/fps:F3}s");

                // 对比三组: 全段首尾 / 去blend后首尾 / 全段首尾+1帧(跨圈)
                (float a, float b, string label)[] pairs =
                {
                    (t1, t2, "全段 s1→s2"),
                    (loopStart, loopEnd, "去blend s1+bi→s2-bo"),
                    (t1, t1 + 1f, "相邻帧 s1→s1+1"),
                };
                foreach (var (pa, pb, label) in pairs)
                {
                    float maxDeg = 0f, sumDeg = 0f;
                    int n = 0, bonesDiff = 0;
                    foreach (var bone in def.BoneAnims)
                    {
                        if (!bone.HasRotationTransform()) continue;
                        var qa = bone.GetInterpolatedRotation(pa, out _, out _);
                        var qb = bone.GetInterpolatedRotation(pb, out _, out _);
                        float dot = Math.Abs(Quaternion.Dot(qa, qb));
                        float deg = (float)(2.0 * Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI);
                        sumDeg += deg; n++;
                        if (deg > 1f) bonesDiff++;
                        if (deg > maxDeg) maxDeg = deg;
                    }
                    Console.WriteLine($"   {label}: 平均差异={sumDeg/Math.Max(1,n):F2}° max={maxDeg:F2}° 差异骨骼={bonesDiff}/{n}");
                }
            }
            return 0;
        }

        // 模式5:在 [s1,s2] 内搜索与 s1 姿势最接近的帧 → 找循环动画的闭合循环区间
        if (args.Length > 2 && args[2] == "loop")
        {
            foreach (var clip in clips)
            {
                var sk = manager.GetAsset<SkeletalAnimation>(clip.Animation);
                if (sk == null) continue;
                var def = sk.Definition?.Data;
                if (def == null) continue;
                float t1 = clip.Source1, t2 = clip.Source2;
                Console.WriteLine($"== {clip.Name} dur={clip.Duration:F3} s1={t1:F0} s2={t2:F0} blendIn={clip.BlendInPeriod:F3} blendOut={clip.BlendOutPeriod:F3} continueWith={clip.ContinueWithAction}");
                // root 位置首尾对比(循环跳变常来自 root 不闭合;root 旋转在 BoneAnims 的 root 骨骼里)
                try
                {
                    var def0 = sk.Definition?.Data;
                    if (def0 != null && def0.RootPositionFrames.Count > 0)
                    {
                        var rp0 = def0.RootPositionFrames.First().Value.Value;   // Vector4
                        var rp1 = def0.RootPositionFrames.Last().Value.Value;
                        float dx = rp0.X - rp1.X, dy = rp0.Y - rp1.Y, dz = rp0.Z - rp1.Z;
                        Console.WriteLine($"   rootPos 首尾距离差={MathF.Sqrt(dx * dx + dy * dy + dz * dz):F4}m (首={rp0} 尾={rp1})");
                    }
                }
                catch (Exception e) { Console.WriteLine($"   (root check failed: {e.Message})"); }
                // 基准姿势 = s1 帧;从 s1+3 开始逐帧对比(跳过前 3 帧避免自匹配)。
                // BoneAnim 无索引,两遍遍历顺序一致即可对齐。
                float bestFrame = -1f, bestDeg = 999f;
                var qRefs = new List<System.Numerics.Quaternion>();
                foreach (var bone in def.BoneAnims)
                {
                    if (!bone.HasRotationTransform()) continue;
                    qRefs.Add(bone.GetInterpolatedRotation(t1, out _, out _));
                }
                for (float f = t1 + 3f; f <= t2; f += 1f)
                {
                    float sum = 0f; int n = 0; float maxDeg = 0f; int qi = 0;
                    foreach (var bone in def.BoneAnims)
                    {
                        if (!bone.HasRotationTransform()) continue;
                        var qa = qRefs[qi++];
                        var qb = bone.GetInterpolatedRotation(f, out _, out _);
                        float dot = Math.Abs(System.Numerics.Quaternion.Dot(qa, qb));
                        float deg = (float)(2.0 * Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI);
                        sum += deg; n++; if (deg > maxDeg) maxDeg = deg;
                    }
                    float avg = sum / Math.Max(1, n);
                    if (avg < bestDeg) { bestDeg = avg; bestFrame = f; }
                    if (f == t1 + 3f || f == t2 || Math.Abs(f - bestFrame) < 0.5f)
                        Console.WriteLine($"   帧 {f:F0}: 平均={avg:F2}° max={maxDeg:F2}°");
                }
                Console.WriteLine($"   → 闭合候选: s1={t1:F0} s2'={bestFrame:F0} (平均={bestDeg:F2}°)" +
                                  $"  ≈24fps帧 {t1*0.8:F1}..{bestFrame*0.8:F1}");
                Console.WriteLine($"     去blend候选: s1+bi={t1 + clip.BlendInPeriod * 30f:F0} s2-bo={t2 - clip.BlendOutPeriod * 30f:F0}");

                // 帧 0 与 s1/s2 的差异:评估 firstFrame=0 是否更闭合(Unity 24fps 取整误差组合)
                if (t1 > 0f)
                    Console.WriteLine($"   帧0 vs 帧1={CalcDegBetween(def, 0f, 1f):F2}° 帧0 vs 帧s2={CalcDegBetween(def, 0f, t2):F2}°");
            }
            return 0;
        }

        // 模式2:按 SkeletalAnimation 名查其引用的所有 clip
        if (args.Length > 2 && args[2] == "sk")
        {
            var allSks = manager.LoadedAssets.OfType<SkeletalAnimation>()
                .Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (allSks.Count == 0)
            {
                Console.Error.WriteLine($"No SkeletalAnimation contains '{filter}'; sample:");
                foreach (var s in manager.LoadedAssets.OfType<SkeletalAnimation>().Take(10))
                    Console.Error.WriteLine($"   {s.Name}");
                return 1;
            }
            Console.Error.WriteLine($"Matching SkeletalAnimations: {allSks.Count}");
            foreach (var sk in allSks)
            {
                Console.WriteLine($"# SkeletalAnimation '{sk.Name}' guid={sk.Guid} duration={sk.Duration} bones={sk.BoneNum}");
                try
                {
                    var def = sk.Definition?.Data;
                    if (def != null)
                    {
                        var rootPos = def.RootPositionFrames;
                        string rp = rootPos.Count > 0
                            ? $"{rootPos.First().Key:F3}..{rootPos.Last().Key:F3} ({rootPos.Count} kf)"
                            : "none";
                        var rotFrames = def.BoneAnims.SelectMany(b => b.RotationFrames).Select(f => f.Key).ToList();
                        string rot = rotFrames.Count > 0
                            ? $"{rotFrames.Min():F3}..{rotFrames.Max():F3} ({rotFrames.Count} kf)"
                            : "none";
                        Console.WriteLine($"   DATA: rootPos={rp} rot={rot}");
                    }
                }
                catch (Exception e) { Console.WriteLine($"   (data load failed: {e.Message})"); }
                var refClips = manager.LoadedAssets.OfType<AnimationClip>()
                    .Where(c => c.Animation == sk.Guid)
                    .OrderBy(c => c.ClipSource1Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.ClipSource2Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (refClips.Count == 0)
                {
                    Console.WriteLine("   (no clips reference it)");
                    continue;
                }
                Console.WriteLine($"# {refClips.Count} clips reference it:");
                foreach (var c in refClips)
                {
                    Console.WriteLine($"  [{c.Name}] src1='{c.ClipSource1Name}' src2='{c.ClipSource2Name}' " +
                        $"dur={c.Duration:F3}s source1={c.Source1:F3} source2={c.Source2:F3} blendIn={c.BlendInPeriod:F3} blendOut={c.BlendOutPeriod:F3} " +
                        $"priority={c.Priority} step={c.StepPoints} combat='{c.CombatParameterId}'");
                }
            }
            return 0;
        }

        foreach (var clip in clips)
        {
            var sk = manager.GetAsset<SkeletalAnimation>(clip.Animation);
            Console.WriteLine($"== {clip.Name}");
            Console.WriteLine($"   clip.Duration={clip.Duration:F4}s  Source1={clip.Source1:F3} Source2={clip.Source2:F3}  Priority={clip.Priority}");
            Console.WriteLine($"   UnknownClipName='{clip.UnknownClipName}' ClipSource1='{clip.ClipSource1Name}' ClipSource2='{clip.ClipSource2Name}'");
            Console.WriteLine($"   Param1={clip.Param1:F3} Param2={clip.Param2:F3} Param3={clip.Param3:F3}  StepPoints={clip.StepPoints}");
            Console.WriteLine($"   BlendIn={clip.BlendInPeriod:F3} BlendOut={clip.BlendOutPeriod:F3}  DoNotInterpolate={clip.DoNotInterpolate}");
            Console.WriteLine($"   BlendsWith={clip.BlendsWithAction} ContinueWith={clip.ContinueWithAction}");
            Console.WriteLine($"   LeftHandPose={clip.LeftHandPose} RightHandPose={clip.RightHandPose}  CombatParam={clip.CombatParameterId}");
            Console.WriteLine($"   Sound={clip.SoundCode} Voice={clip.VoiceCode} Facial={clip.FacialAnimationId}");
            if (clip.ClipUsages.Count > 0)
            {
                foreach (var u in clip.ClipUsages)
                    Console.WriteLine($"   usage[{u.Type}]: {Describe(u)}");
            }
            if (sk != null)
            {
                Console.WriteLine($"   -> skName='{sk.Name}' skeleton={sk.Skeleton} duration(frames?)={sk.Duration} bones={sk.BoneNum}");
                // 尝试加载定义数据(关键帧时间范围)
                try
                {
                    var def = sk.Definition?.Data;
                    if (def != null)
                    {
                        var rootPos = def.RootPositionFrames;
                        string rp = rootPos.Count > 0
                            ? $"{rootPos.First().Key:F3}..{rootPos.Last().Key:F3} ({rootPos.Count} frames)"
                            : "none";
                        var rotFrames = def.BoneAnims.SelectMany(b => b.RotationFrames).Select(f => f.Key).ToList();
                        string rot = rotFrames.Count > 0
                            ? $"{rotFrames.Min():F3}..{rotFrames.Max():F3} ({rotFrames.Count} frames)"
                            : "none";
                        Console.WriteLine($"   keyframes: rootPos={rp}  rotTimeRange={rot}");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"   (definition load failed: {e.Message})");
                }
            }
            else
            {
                Console.WriteLine($"   -> animation guid {clip.Animation} NOT RESOLVED");
            }
        }
        return 0;
    }

    /// <summary>两帧之间全部骨骼旋转的平均差异(度)。</summary>
    static float CalcDegBetween(AnimationDefinitionData def, float fa, float fb)
    {
        float sum = 0f; int n = 0;
        foreach (var bone in def.BoneAnims)
        {
            if (!bone.HasRotationTransform()) continue;
            var qa = bone.GetInterpolatedRotation(fa, out _, out _);
            var qb = bone.GetInterpolatedRotation(fb, out _, out _);
            float dot = Math.Abs(System.Numerics.Quaternion.Dot(qa, qb));
            float deg = (float)(2.0 * Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI);
            sum += deg; n++;
        }
        return sum / Math.Max(1, n);
    }

    static string Describe(AnimationClip.ClipUsage u)
    {
        switch (u)
        {
            case AnimationClip.DisplacementUsage d:
                return $"vec={d.DisplacementVector} endProgress={d.DisplacementEndProgress}";
            case AnimationClip.BipMovIkUsage b:
                return $"loopDisp={b.LoopDisplacement} snapStart={b.SnappingStart0}/{b.SnappingStart1} snapDur={b.SnappingDuration0}/{b.SnappingDuration1} adjStart={b.AdjustingStart0}/{b.AdjustingStart1}";
            case AnimationClip.QuadMovementUsage q:
                return $"loopDisp={q.LoopDisplacement} pace={q.PaceSwitchLimitMin}..{q.PaceSwitchLimitMax}";
            case AnimationClip.BlendUsage bl:
                return $"blendStart={bl.BlendStartProgress} blendEnd={bl.BlendEndProgress}";
            default:
                return u.Type;
        }
    }
}
