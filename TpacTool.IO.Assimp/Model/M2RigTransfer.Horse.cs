using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using JetBrains.Annotations;
using TpacTool.Lib;
using Mesh = TpacTool.Lib.Mesh;

namespace TpacTool.IO.Assimp
{
	/// <summary>
	/// Bannerlord's horse_skeleton onto the vanilla Medieval 2 horse (fs_horse, Horse.glb): the mesh is
	/// scaled to the m2 horse's body length and centred on it, its weights moved onto the 23 m2 bones, and
	/// each leg segment, the neck and the tail fitted onto the m2 joints.
	/// <para/>
	/// Horse.glb is in the same space as the human armatures (the toolkit's import turned 180 degrees about
	/// z), where the m2 horse faces +y with its left legs on -x, as bannerlord's horse does; should the two
	/// face opposite ways, the horse is turned about z to match.
	/// </summary>
	public static partial class M2RigTransfer
	{
		// bannerlord horse bone -> m2 horse bone ("{s}" is l / r, "{S}" Left / Right)
		private static readonly Dictionary<string, string> HorseBoneMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "horsepelvis", "bone_H_Saddle" },
			{ "horsespine1", "bone_H_Saddle" },
			{ "horsespine2", "bone_Spine" },
			{ "horsespine3", "bone_Spine1" },
			{ "horseneck1", "bone_Neck" },
			{ "horseneck2", "bone_Neck" },
			{ "horse_head", "bone_Head" },
			{ "horsetail1", "bone_Tail1" },
			{ "horsetail2", "bone_Tail2" },
			{ "horsetail3", "bone_Tail2" },
			// front legs: the shoulder blade goes with the upper leg
			{ "horse{s}leg1", "bone_{S}Arm" },
			{ "horse{s}leg2", "bone_{S}Arm" },
			{ "horse{s}leg3", "bone_{S}ForeArm" },
			{ "horse{s}leg4", "bone_{S}Hand" },
			{ "horse{s}legankle", "bone_{S}FingerBase" },
			{ "horse{s}fronthoof", "bone_{S}FingerBase" },
			// hind legs
			{ "horse{s}femur", "bone_{S}UpLeg" },
			{ "horse{s}tibia", "bone_{S}Leg" },
			{ "horse{s}largecannon", "bone_{S}Foot" },
			{ "horse{s}phalanx", "bone_{S}ToeBase" },
			{ "horse{s}rearhoof", "bone_{S}ToeBase" },
			{ "horse{s}rearthoof", "bone_{S}ToeBase" }, // the right one is spelt so in the game
		};

		/// <summary>Whether a skeleton is bannerlord's horse (horsepelvis, femurs and front legs).</summary>
		public static bool IsBannerlordHorse([NotNull] Skeleton skeleton)
		{
			var bones = skeleton.Definition.Data.Bones;
			return IndexOf(bones, "horsepelvis") >= 0 && IndexOf(bones, "horselfemur") >= 0 &&
			       IndexOf(bones, "horserleg2") >= 0;
		}

		private static string MapHorseName(string blName)
		{
			var n = blName.ToLowerInvariant();
			if (HorseBoneMap.TryGetValue(n, out var direct))
				return direct;
			if (n.StartsWith("horsel") || n.StartsWith("horser"))
			{
				var side = n[5];
				if (HorseBoneMap.TryGetValue("horse{s}" + n.Substring(6), out var sided))
					return sided.Replace("{S}", side == 'l' ? "Left" : "Right");
			}
			return null;
		}

		/// <summary>
		/// Re-homes horse meshes onto the m2 horse in place, like <see cref="Apply"/> does for humans. Dispose
		/// the result to restore the original mesh data. <paramref name="log"/> gets the bone mapping and fit.
		/// </summary>
		public static IDisposable ApplyHorse([NotNull] Skeleton bannerlordHorse, [NotNull] Skeleton m2Horse,
			[NotNull] IEnumerable<Mesh> meshes, out List<string> log)
		{
			log = new List<string>();
			var blBones = bannerlordHorse.Definition.Data.Bones;
			var m2Bones = m2Horse.Definition.Data.Bones;
			var blPos = Globals(blBones, TPoseConverter.IsIgnoreScaleSkeleton(bannerlordHorse)).Select(m => m.Translation).ToArray();
			var m2Pos = Globals(m2Bones, false).Select(m => m.Translation).ToArray();

			Vector3 Mid(Vector3[] pos, List<BoneNode> bones, string a, string b)
			{
				int i = IndexOf(bones, a), j = IndexOf(bones, b);
				if (i < 0 || j < 0)
					throw new InvalidOperationException($"{a} / {b} missing from {(bones == blBones ? bannerlordHorse.Name : m2Horse.Name)}");
				return (pos[i] + pos[j]) * 0.5f;
			}

			// the body: hips (femurs / up legs) and shoulders (upper front legs)
			var blHips = Mid(blPos, blBones, "horselfemur", "horserfemur");
			var blShoulders = Mid(blPos, blBones, "horselleg2", "horserleg2");
			var m2Hips = Mid(m2Pos, m2Bones, "bone_LeftUpLeg", "bone_RightUpLeg");
			var m2Shoulders = Mid(m2Pos, m2Bones, "bone_LeftArm", "bone_RightArm");

			// turn about z when the two face opposite ways
			var blForward = new Vector2(blShoulders.X - blHips.X, blShoulders.Y - blHips.Y);
			var m2Forward = new Vector2(m2Shoulders.X - m2Hips.X, m2Shoulders.Y - m2Hips.Y);
			var turned = Vector2.Dot(blForward, m2Forward) < 0;
			var turn = turned ? Matrix4x4.CreateRotationZ((float) Math.PI) : Matrix4x4.Identity;
			if (turned)
				log.Add("turned 180 degrees about z to face the m2 horse's way");

			var scale = (m2Shoulders - m2Hips).Length() / (blShoulders - blHips).Length();
			var origin = (blHips + blShoulders) * 0.5f;
			var target = (m2Hips + m2Shoulders) * 0.5f;
			Vector3 MapPoint(Vector3 p) => Vector3.Transform((p - origin) * scale, turn) + target;
			Vector3 MapDirection(Vector3 d) => Vector3.TransformNormal(d, turn);
			log.Add($"scaled {scale:0.00}x to the m2 horse's body length");

			var mapped = blPos.Select(MapPoint).ToArray();
			var leftBl = IndexOf(blBones, "horselleg2");
			var leftM2 = IndexOf(m2Bones, "bone_LeftArm");
			if (Math.Sign(mapped[leftBl].X - target.X) != Math.Sign(m2Pos[leftM2].X - target.X))
				log.Add("warning: the left legs land on the m2 horse's right side");

			// bone index map
			var map = new int[blBones.Count];
			var mapLog = new List<string>();
			var hub = IndexOf(m2Bones, "bone_H_Saddle");
			for (int i = 0; i < blBones.Count; i++)
			{
				var m2Name = MapHorseName(blBones[i].Name);
				var j = m2Name != null ? IndexOf(m2Bones, m2Name) : -1;
				if (j < 0)
				{
					var p = mapped[i];
					j = Enumerable.Range(0, m2Bones.Count).OrderBy(k => (m2Pos[k] - p).LengthSquared()).First();
					mapLog.Add($"{blBones[i].Name}->{m2Bones[j].Name} (nearest)");
				}
				else
				{
					mapLog.Add($"{blBones[i].Name}->{m2Bones[j].Name}");
				}
				map[i] = j >= 0 ? j : Math.Max(hub, 0);
			}
			log.Add("bones: " + string.Join(", ", mapLog));

			var fit = ComputeHorseFit(blBones, m2Bones, mapped, m2Pos, log);

			var scope = new RestoreScope();
			try
			{
				var visited = new HashSet<MeshEditData>();
				foreach (var mesh in meshes)
				{
					var data = mesh?.EditData?.Data;
					if (data == null || !visited.Add(data))
						continue;
					scope.Save(data);
					Remap(data, map, m2Bones.Count, MapPoint, MapDirection, fit, false);
				}
			}
			catch
			{
				scope.Dispose();
				throw;
			}
			return scope;
		}

		/// <summary>
		/// Per bannerlord horse bone, a transform (in the mapped space) that puts the legs, neck and tail onto
		/// the m2 joints: each segment is turned, moved and stretched so both of its joints land on the m2
		/// joints, the last joint of a chain keeps its segment's turn and moves onto its m2 joint, and the
		/// bones in between follow. The body stays as scaled.
		/// </summary>
		private static Matrix4x4[] ComputeHorseFit(List<BoneNode> bl, List<BoneNode> m2, Vector3[] p, Vector3[] q,
			List<string> log)
		{
			var fit = Enumerable.Repeat(Matrix4x4.Identity, bl.Count).ToArray();
			int B(string n) => IndexOf(bl, n);
			int M(string n) => IndexOf(m2, n);
			float maxMove = 0;
			var fitted = new List<string>();

			Quaternion? Segment(int a, int b, int ma, int mb)
			{
				if (a < 0 || b < 0 || ma < 0 || mb < 0)
					return null;
				var from = p[b] - p[a];
				var to = q[mb] - q[ma];
				if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f)
					return null;
				var rotation = FromTo(Vector3.Normalize(from), Vector3.Normalize(to));
				var k = Math.Max(0.75f, Math.Min(1.33f, to.Length() / from.Length()));
				fit[a] = Matrix4x4.CreateTranslation(-p[a]) * Matrix4x4.CreateFromQuaternion(rotation) *
				         AxisScale(Vector3.Normalize(to), k) * Matrix4x4.CreateTranslation(q[ma]);
				maxMove = Math.Max(maxMove, Math.Max((q[ma] - p[a]).Length(), (q[mb] - p[b]).Length()));
				return rotation;
			}

			void End(int a, int ma, Quaternion? rotation)
			{
				if (a < 0 || ma < 0)
					return;
				fit[a] = Matrix4x4.CreateTranslation(-p[a]) *
				         Matrix4x4.CreateFromQuaternion(rotation ?? Quaternion.Identity) *
				         Matrix4x4.CreateTranslation(q[ma]);
				maxMove = Math.Max(maxMove, (q[ma] - p[a]).Length());
			}

			void Follow(int bone, int leader)
			{
				if (bone >= 0 && leader >= 0)
					fit[bone] = fit[leader];
			}

			// a chain of bannerlord joints onto m2 joints; the last pair is the end joint
			bool Chain(string name, string[] blJoints, string[] m2Joints, string[] followers)
			{
				var a = blJoints.Select(B).ToArray();
				var m = m2Joints.Select(M).ToArray();
				Quaternion? last = null;
				for (int i = 0; i + 1 < a.Length; i++)
				{
					last = Segment(a[i], a[i + 1], m[i], m[i + 1]);
					if (!last.HasValue)
						return false;
				}
				End(a[a.Length - 1], m[m.Length - 1], last);
				foreach (var follower in followers)
				{
					var parts = follower.Split('>');
					Follow(B(parts[0]), B(parts[1]));
				}
				fitted.Add(name);
				return true;
			}

			foreach (var side in new[] { "l", "r" })
			{
				var S = side == "l" ? "Left" : "Right";
				Chain(side + " front leg",
					new[] { $"horse{side}leg2", $"horse{side}leg3", $"horse{side}leg4", $"horse{side}legankle" },
					new[] { $"bone_{S}Arm", $"bone_{S}ForeArm", $"bone_{S}Hand", $"bone_{S}FingerBase" },
					new[] { $"horse{side}fronthoof>horse{side}legankle" });
				var hoof = B($"horse{side}rearhoof") >= 0 ? $"horse{side}rearhoof" : $"horse{side}rearthoof";
				Chain(side + " hind leg",
					new[] { $"horse{side}femur", $"horse{side}tibia", $"horse{side}largecannon", $"horse{side}phalanx" },
					new[] { $"bone_{S}UpLeg", $"bone_{S}Leg", $"bone_{S}Foot", $"bone_{S}ToeBase" },
					new[] { $"{hoof}>horse{side}phalanx" });
			}
			Chain("neck", new[] { "horseneck1", "horse_head" }, new[] { "bone_Neck", "bone_Head" },
				new[] { "horseneck2>horseneck1" });
			Chain("tail", new[] { "horsetail1", "horsetail2" }, new[] { "bone_Tail1", "bone_Tail2" },
				new[] { "horsetail3>horsetail2" });

			if (fitted.Count > 0)
				log.Add($"fitted {string.Join(", ", fitted)} onto the m2 joints (moved up to {maxMove * 100:0.0} cm)");
			return fit;
		}
	}
}
