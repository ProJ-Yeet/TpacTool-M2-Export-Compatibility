using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using TpacTool.Lib;

namespace TpacTool
{
    /// Samples an AnimationClip source range in clip-local seconds.
    public sealed class AnimationClipPreview
    {
        private AnimationDefinitionData _animation;
        private SkeletonDefinitionData _skeleton;
        private Matrix4x4[] _rest;
        private int[] _parents;
        private int[] _order;
        private float _start;
        private float _end;
        private bool _step;

        public float Duration { get; private set; }
        public IReadOnlyList<int> ParentIndices { get; private set; } = new int[0];
        public string Error { get; private set; }

        public static AnimationClipPreview Create(AnimationClip clip, IEnumerable<AssetItem> assets)
        {
            var result = new AnimationClipPreview();
            try
            {
                result.Initialize(clip, assets);
                result.Sample(0f);
            }
            catch (Exception exception)
            {
                result.Error = exception.Message;
                result.Duration = 0f;
                result.ParentIndices = new int[0];
            }
            return result;
        }

        private void Initialize(AnimationClip clip, IEnumerable<AssetItem> assets)
        {
            if (clip == null)
                throw new InvalidDataException("未选择动画剪辑。");

            var all = (assets ?? Enumerable.Empty<AssetItem>()).ToList();
            var source = all.OfType<SkeletalAnimation>().FirstOrDefault(item => item.Guid == clip.Animation);
            if (source == null)
                throw new InvalidDataException("未加载剪辑引用的源动画。");
            _animation = source.Definition == null ? null : source.Definition.Data;
            if (_animation == null)
                throw new InvalidDataException("源动画没有可预览的关键帧数据。");

            var skeletonAsset = all.OfType<Skeleton>().FirstOrDefault(item => item.Guid == source.Skeleton);
            _skeleton = skeletonAsset == null || skeletonAsset.Definition == null ? null : skeletonAsset.Definition.Data;
            if (_skeleton == null || _skeleton.Bones.Count == 0)
                throw new InvalidDataException("未加载源动画引用的骨架。");

            if (!Finite(clip.Source1) || !Finite(clip.Source2) || !Finite(clip.Duration))
                throw new InvalidDataException("剪辑的时间范围无效。");

            var maxFrame = -1f;
            ValidateTrack(_animation.RootPositionFrames, value => Finite(value.X) && Finite(value.Y) && Finite(value.Z) && Finite(value.W), ref maxFrame);
            ValidateTrack(_animation.RootScaleFrames, value => Finite(value.X) && Finite(value.Y) && Finite(value.Z), ref maxFrame);
            foreach (var bone in _animation.BoneAnims)
            {
                if (bone == null)
                    continue;
                ValidateTrack(bone.PositionFrames, value => Finite(value.X) && Finite(value.Y) && Finite(value.Z) && Finite(value.W), ref maxFrame);
                ValidateTrack(bone.RotationFrames, value => Finite(value.X) && Finite(value.Y) && Finite(value.Z) && Finite(value.W) && value.LengthSquared() > 0.000001f, ref maxFrame);
            }

            if (maxFrame < 0f)
                maxFrame = Math.Max(0f, source.Duration);
            _start = Math.Max(0f, clip.Source1);
            _end = clip.Source2 < 0f ? maxFrame : Math.Max(0f, clip.Source2);
            Duration = clip.Duration > 0f ? clip.Duration : Math.Abs(_end - _start) / 30f;
            _step = clip.DoNotInterpolate;

            _parents = _skeleton.CreateParentLookup();
            _rest = _skeleton.Bones.Select(bone => bone.RestFrame).ToArray();
            var visited = new byte[_parents.Length];
            var order = new List<int>(_parents.Length);
            for (var i = 0; i < _parents.Length; i++)
                Visit(i, visited, order);
            _order = order.ToArray();
            ParentIndices = Array.AsReadOnly(_parents);
        }

        private void Visit(int index, byte[] visited, List<int> order)
        {
            if (visited[index] == 2)
                return;
            if (visited[index] == 1)
                throw new InvalidDataException("骨架的父子关系包含循环。");
            visited[index] = 1;
            if (_parents[index] >= 0)
                Visit(_parents[index], visited, order);
            visited[index] = 2;
            order.Add(index);
        }

        public Matrix4x4[] Sample(float time)
        {
            if (Error != null)
                return new Matrix4x4[0];
            if (!Finite(time))
                throw new ArgumentOutOfRangeException(nameof(time));

            var progress = Duration > 0f ? Math.Max(0f, Math.Min(Duration, time)) / Duration : 0f;
            var frame = _start + (_end - _start) * progress;
            var rootPosition = SampleTrack(_animation.RootPositionFrames, frame, Vector4.Zero, Vector4.Lerp);
            var rootScale = SampleTrack(_animation.RootScaleFrames, frame, Vector3.One, Vector3.Lerp);
            var root = Matrix4x4.CreateScale(rootScale) * Matrix4x4.CreateTranslation(rootPosition.X, rootPosition.Y, rootPosition.Z);
            var matrices = new Matrix4x4[_skeleton.Bones.Count];

            foreach (var index in _order)
            {
                var rest = _rest[index];
                rest.M44 = 1f;
                if (!Matrix4x4.Decompose(rest, out var restScale, out var restRotation, out var restTranslation))
                    throw new InvalidDataException("骨架包含无效的静止变换。");

                var bone = index < _animation.BoneAnims.Count ? _animation.BoneAnims[index] : null;
                var rotation = restRotation;
                var offset = Vector4.Zero;
                if (bone != null)
                {
                    if (bone.RotationFrames.Count > 0)
                        rotation = Quaternion.Normalize(SampleTrack(bone.RotationFrames, frame, Quaternion.Identity, Quaternion.Slerp));
                    if (bone.PositionFrames.Count > 0)
                        offset = SampleTrack(bone.PositionFrames, frame, Vector4.Zero, Vector4.Lerp);
                }

                var local = Matrix4x4.CreateScale(restScale)
                    * Matrix4x4.CreateFromQuaternion(rotation)
                    * Matrix4x4.CreateTranslation(restTranslation + new Vector3(offset.X, offset.Y, offset.Z));
                matrices[index] = local * (_parents[index] >= 0 ? matrices[_parents[index]] : root);
                if (!Finite(matrices[index]))
                    throw new InvalidDataException("动画姿态包含无效的骨骼变换。");
            }
            return matrices;
        }

        private T SampleTrack<T>(SortedList<float, AnimationFrame<T>> track, float frame, T fallback, Func<T, T, float, T> interpolate) where T : struct
        {
            if (track.Count == 0)
                return fallback;
            if (frame <= track.Keys[0])
                return track.Values[0].Value;
            if (frame >= track.Keys[track.Count - 1])
                return track.Values[track.Count - 1].Value;

            var low = 0;
            var high = track.Count - 1;
            while (high - low > 1)
            {
                var middle = (low + high) / 2;
                if (track.Keys[middle] <= frame)
                    low = middle;
                else
                    high = middle;
            }
            if (_step)
                return track.Values[low].Value;
            var amount = (frame - track.Keys[low]) / (track.Keys[high] - track.Keys[low]);
            return interpolate(track.Values[low].Value, track.Values[high].Value, amount);
        }

        private static void ValidateTrack<T>(SortedList<float, AnimationFrame<T>> track, Func<T, bool> valid, ref float maxFrame) where T : struct
        {
            foreach (var pair in track)
            {
                if (!Finite(pair.Key) || !valid(pair.Value.Value))
                    throw new InvalidDataException("动画关键帧数据无效。");
                maxFrame = Math.Max(maxFrame, pair.Key);
            }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Matrix4x4 matrix) =>
            Finite(matrix.M11) && Finite(matrix.M12) && Finite(matrix.M13) && Finite(matrix.M14) &&
            Finite(matrix.M21) && Finite(matrix.M22) && Finite(matrix.M23) && Finite(matrix.M24) &&
            Finite(matrix.M31) && Finite(matrix.M32) && Finite(matrix.M33) && Finite(matrix.M34) &&
            Finite(matrix.M41) && Finite(matrix.M42) && Finite(matrix.M43) && Finite(matrix.M44);
    }
}