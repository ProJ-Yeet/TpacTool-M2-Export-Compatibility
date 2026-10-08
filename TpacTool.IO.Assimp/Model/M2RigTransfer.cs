using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Assimp;
using JetBrains.Annotations;
using TpacTool.Lib;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Mesh = TpacTool.Lib.Mesh;

namespace TpacTool.IO.Assimp
{
	/// <summary>
	/// Moves a Bannerlord human rig onto a Medieval II: Total War skeleton (the .glb armatures of the
	/// Medieval 2 Toolkit Blender addon): the mesh is placed in the M2 skeleton's space and every vertex
	/// weight is moved from its Bannerlord bone to the matching M2 bone, merging bones M2 doesn't have
	/// (twist bones, toes, fingers, the extra spine bone and the neck).
	/// <para/>
	/// The M2 skeleton is in T-pose, so the Bannerlord data must already be in T-pose
	/// (<see cref="TPoseConverter.Apply"/>) when <see cref="Apply"/> is called.
	/// <para/>
	/// Spaces: TpacTool writes Bannerlord coordinates (z up, characters face +y, r_* bones on +x) and
	/// Blender imports them unchanged. The toolkit's armatures land in Blender (through its glTF importer:
	/// glTF (x, y, z) -> (x, -z, y)) with the pelvis at the origin and the R bones on +x, facing the same
	/// way as an imported Bannerlord model. So the model is only moved (pelvis onto bone_pelvis), r_*
	/// bones go to bone_R* and nothing is mirrored. (mirror: true is the old behaviour - mirrored in y,
	/// facing the other way, with the winding flipped.)
	/// </summary>
	public static class M2RigTransfer
	{
		// bannerlord bone -> m2 bone (case-insensitive; "{s}" is the side: R / L)
		private static readonly Dictionary<string, string> BoneMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			// spine: each bannerlord bone goes to the m2 bone whose segment holds its midpoint
			{ "pelvis", "bone_pelvis" },
			{ "spine", "bone_pelvis" },
			{ "spine1", "bone_abs" },
			{ "spine2", "bone_torso" },
			{ "neck", "bone_torso" },
			{ "head", "bone_head" },
			{ "head_13", "bone_head" },
			// arms
			{ "{s}_clavicle", "bone_{S}clavical" },
			{ "{s}_upperarm_twist", "bone_{S}upperarm" },
			{ "{s}_upperarm_twist1", "bone_{S}upperarm" },
			{ "{s}_foretwist", "bone_{S}elbow" },
			{ "{s}_foretwist1", "bone_{S}elbow" },
			{ "{s}_hand", "bone_{S}hand" },
			{ "{s}_finger0", "bone_{S}hand" },
			// legs
			{ "{s}_thigh", "bone_{S}Thigh" },
			{ "{s}_calf", "bone_{S}lowerleg" },
			{ "{s}_foot", "bone_{S}foot" },
			{ "{s}_toe0", "bone_{S}foot" },
		};

		/// <summary>Bones that only carry props: never a fallback target for body weights.</summary>
		private static bool IsPropBone(string name)
		{
			var n = name.ToLowerInvariant();
			return n.Contains("weapon") || n.Contains("jaw") || n.Contains("eyebrow") || n.Contains("shield") ||
			       n.Contains("quiver") || n.Contains("arrow");
		}

		#region M2 skeleton loading

		/// <summary>
		/// The Medieval 2 skeletons shipped with TpacTool (from the Medieval 2 Blender Toolkit's armatures):
		/// &lt;name&gt;.glb holds the skeleton, &lt;name&gt;_Sample.glb a sample body and the skeleton's animations.
		/// </summary>
		public static string BundledSkeletonsDir =>
			Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "M2Skeletons");

		/// <summary>
		/// The skeleton .glb files (Sword, Spear, Archer, ...) in a Medieval 2 Toolkit armatures folder,
		/// without the *_Sample and Equipment files.
		/// </summary>
		public static List<string> FindSkeletonFiles([CanBeNull] string armaturesDir)
		{
			if (string.IsNullOrWhiteSpace(armaturesDir) || !Directory.Exists(armaturesDir))
				return new List<string>();
			return Directory.GetFiles(armaturesDir, "*.glb")
				.Where(f =>
				{
					var stem = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
					return !stem.EndsWith("_sample") && stem != "equipment";
				})
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Loads an M2 armature .glb as a TpacTool skeleton, converted into the space TpacTool exports in
		/// (see the class comment), so that it lands exactly on the toolkit's own import of the .glb.
		/// </summary>
		public static Skeleton LoadSkeleton(string glbPath)
		{
			return LoadSkeleton(glbPath, false, out _);
		}

		/// <summary>
		/// Loads an M2 armature and, with <paramref name="includeAnimations"/>, its animations: from the
		/// &lt;name&gt;_Sample.glb next to it when there is one (the samples carry all of the skeleton's
		/// clips), else from the file itself. The animations are converted into the same space as the
		/// skeleton, so they play exactly as on the toolkit's own import.
		/// </summary>
		public static Skeleton LoadSkeleton(string glbPath, bool includeAnimations, out List<Animation> animations)
		{
			animations = new List<Animation>();
			var skeleton = LoadSkeleton(glbPath, null);
			if (!includeAnimations)
				return skeleton;

			var samplePath = Path.Combine(Path.GetDirectoryName(glbPath) ?? string.Empty,
				Path.GetFileNameWithoutExtension(glbPath) + "_Sample.glb");
			var source = File.Exists(samplePath) ? samplePath : glbPath;
			LoadSkeleton(source, animations);

			// only keep channels of bones the exported skeleton has (sample body nodes etc. are dropped)
			var boneNames = new HashSet<string>(skeleton.Definition.Data.Bones.Select(b => b.Name));
			foreach (var animation in animations)
				animation.NodeAnimationChannels.RemoveAll(c => !boneNames.Contains(c.NodeName));
			animations.RemoveAll(a => a.NodeAnimationChannelCount == 0);
			return skeleton;
		}

		private static Skeleton LoadSkeleton(string glbPath, List<Animation> animationsOut)
		{
			if (!AssimpModelExporter.IsAssimpAvailable())
				throw new InvalidOperationException("Assimp is not available");
			Scene scene;
			using (var context = new AssimpContext())
				scene = context.ImportFile(glbPath, PostProcessSteps.None);

			// the armature node is the parent of bone_pelvis (or of the topmost bone_* node)
			var pelvis = FindNode(scene.RootNode, n => n.Name.Equals("bone_pelvis", StringComparison.OrdinalIgnoreCase)) ??
			             FindNode(scene.RootNode, n => n.Name.StartsWith("bone_", StringComparison.OrdinalIgnoreCase));
			if (pelvis == null)
				throw new InvalidDataException(Path.GetFileName(glbPath) + " has no bone_* nodes");
			var armature = pelvis.Parent;

			// glTF (x, y, z) -> blender (x, -z, y); as a row-vector matrix
			var gltfToExport = new Matrix4x4(
				1, 0, 0, 0,
				0, 0, 1, 0,
				0, -1, 0, 0,
				0, 0, 0, 1);

			var data = new SkeletonDefinitionData { Name = armature?.Name ?? Path.GetFileNameWithoutExtension(glbPath) };
			var globals = new Dictionary<Node, Matrix4x4>();
			void AddBone(Node node, BoneNode parent)
			{
				if (node.MeshCount > 0)
					return; // stray meshes (sample bodies, an icosphere) are not bones
				var global = ToNumerics(GlobalTransform(node)) * gltfToExport;
				globals[node] = global;
				var local = global;
				if (parent != null)
				{
					Matrix4x4.Invert(globals[node.Parent], out var invParent);
					local = global * invParent;
				}
				var bone = new BoneNode { Name = node.Name, Parent = parent, RestFrame = local };
				data.Bones.Add(bone);
				foreach (var child in node.Children)
					AddBone(child, bone);
			}
			AddBone(pelvis, null);

			if (animationsOut != null && scene.HasAnimations)
			{
				// bone-local keys don't change with the space (parent and child move together); only the
				// root bone's keys are relative to the armature and have to go through armature -> export
				var rootFrame = (armature != null ? ToNumerics(GlobalTransform(armature)) : Matrix4x4.Identity) *
				                gltfToExport;
				Matrix4x4.Decompose(rootFrame, out _, out var rootRotation, out _);
				foreach (var animation in scene.Animations)
				{
					// assimp's glTF importer stores key times in milliseconds (glTF seconds * 1000) but does not
					// report 1000 ticks per second (it reports ~24), and its fbx exporter writes key times as
					// seconds whatever the ticks per second say. Measured on Sword_Sample: the 8.25 s clip's
					// last key is 8250. So: key / 1000 = seconds
					const double ticksPerSecond = 1000.0;
					foreach (var channel in animation.NodeAnimationChannels)
					{
						for (int k = 0; k < channel.PositionKeyCount; k++)
							channel.PositionKeys[k] = new VectorKey(channel.PositionKeys[k].Time / ticksPerSecond, channel.PositionKeys[k].Value);
						for (int k = 0; k < channel.RotationKeyCount; k++)
							channel.RotationKeys[k] = new QuaternionKey(channel.RotationKeys[k].Time / ticksPerSecond, channel.RotationKeys[k].Value);
						for (int k = 0; k < channel.ScalingKeyCount; k++)
							channel.ScalingKeys[k] = new VectorKey(channel.ScalingKeys[k].Time / ticksPerSecond, channel.ScalingKeys[k].Value);
					}
					animation.DurationInTicks = animation.DurationInTicks / ticksPerSecond;
					animation.TicksPerSecond = 1000.0;

					foreach (var channel in animation.NodeAnimationChannels)
					{
						if (channel.NodeName != pelvis.Name)
							continue;
						for (int k = 0; k < channel.PositionKeyCount; k++)
						{
							var key = channel.PositionKeys[k];
							var p = Vector3.Transform(new Vector3(key.Value.X, key.Value.Y, key.Value.Z), rootFrame);
							channel.PositionKeys[k] = new VectorKey(key.Time, new Vector3D(p.X, p.Y, p.Z));
						}
						for (int k = 0; k < channel.RotationKeyCount; k++)
						{
							var key = channel.RotationKeys[k];
							var q = new Quaternion(key.Value.X, key.Value.Y, key.Value.Z, key.Value.W);
							// row-vector convention: local rotation first, then the root frame
							var r = Quaternion.Concatenate(q, rootRotation);
							channel.RotationKeys[k] = new QuaternionKey(key.Time,
								new global::Assimp.Quaternion(r.W, r.X, r.Y, r.Z));
						}
					}
					animationsOut.Add(animation);
				}
			}

			var name = armature != null && !string.IsNullOrEmpty(armature.Name) && armature.Name != "RootNode"
				? armature.Name
				: "Armature_" + Path.GetFileNameWithoutExtension(glbPath);
			return new Skeleton { Name = name, Definition = new ExternalLoader<SkeletonDefinitionData>(data) };
		}

		private static Node FindNode(Node node, Func<Node, bool> predicate)
		{
			if (predicate(node))
				return node;
			foreach (var child in node.Children)
			{
				var result = FindNode(child, predicate);
				if (result != null)
					return result;
			}
			return null;
		}

		private static global::Assimp.Matrix4x4 GlobalTransform(Node node)
		{
			// assimpnet's a * b means "a, then b" (b x a in column-vector terms)
			var m = node.Transform;
			for (var p = node.Parent; p != null; p = p.Parent)
				m = m * p.Transform;
			return m;
		}

		private static Matrix4x4 ToNumerics(global::Assimp.Matrix4x4 m)
		{
			// assimp is column-vector (translation in A4, B4, C4); system.numerics is row-vector
			return new Matrix4x4(
				m.A1, m.B1, m.C1, m.D1,
				m.A2, m.B2, m.C2, m.D2,
				m.A3, m.B3, m.C3, m.D3,
				m.A4, m.B4, m.C4, m.D4);
		}

		#endregion

		#region Transfer

		/// <summary>
		/// Re-homes the meshes onto <paramref name="m2Skeleton"/> in place. Dispose the result to restore
		/// the original mesh data. <paramref name="log"/> receives the bone mapping and fit notes.
		/// </summary>
		public static IDisposable Apply([NotNull] Skeleton bannerlordSkeleton, [NotNull] Skeleton m2Skeleton,
			[NotNull] IEnumerable<Mesh> meshes, out List<string> log, bool fitLimbs = true, bool mirror = false)
		{
			log = new List<string>();
			var blBones = bannerlordSkeleton.Definition.Data.Bones;
			var m2Bones = m2Skeleton.Definition.Data.Bones;
			var ignoreScale = TPoseConverter.IsIgnoreScaleSkeleton(bannerlordSkeleton);
			var blPos = Globals(blBones, ignoreScale).Select(m => m.Translation).ToArray();
			var m2Pos = Globals(m2Bones, false).Select(m => m.Translation).ToArray();

			var blPelvis = IndexOf(blBones, "pelvis");
			var m2Pelvis = IndexOf(m2Bones, "bone_pelvis");
			if (blPelvis < 0 || m2Pelvis < 0)
				throw new InvalidOperationException("Both skeletons need a pelvis bone (pelvis / bone_pelvis)");

			// bannerlord space -> m2 export space: pelvis onto the m2 pelvis, same orientation (r_* stays on
			// +x with the m2 R bones). mirror flips y as well, turning the model around mirrored
			var origin = blPos[blPelvis];
			var target = m2Pos[m2Pelvis];
			var sy = mirror ? -1f : 1f;
			Vector3 MapPoint(Vector3 p) => new Vector3(p.X - origin.X, sy * (p.Y - origin.Y), p.Z - origin.Z) + target;
			Vector3 MapDirection(Vector3 d) => new Vector3(d.X, sy * d.Y, d.Z);

			// bone index map
			var map = new int[blBones.Count];
			var mapLog = new List<string>();
			double worstFit = 0;
			string worstFitName = null;
			for (int i = 0; i < blBones.Count; i++)
			{
				var m2Name = MapName(blBones[i].Name);
				var j = m2Name != null ? IndexOf(m2Bones, m2Name) : -1;
				if (j < 0)
				{
					// bone without a known counterpart: the nearest m2 joint
					var p = MapPoint(blPos[i]);
					j = Enumerable.Range(0, m2Bones.Count)
						.Where(k => !IsPropBone(m2Bones[k].Name))
						.OrderBy(k => (m2Pos[k] - p).LengthSquared())
						.First();
					mapLog.Add($"{blBones[i].Name}->{m2Bones[j].Name} (nearest)");
				}
				else
				{
					mapLog.Add($"{blBones[i].Name}->{m2Bones[j].Name}");
					if (!IsMergedBone(blBones[i].Name))
					{
						// a joint both skeletons have: measure how well they line up
						var distance = (MapPoint(blPos[i]) - m2Pos[j]).Length();
						if (distance > worstFit)
						{
							worstFit = distance;
							worstFitName = $"{blBones[i].Name}/{m2Bones[j].Name}";
						}
					}
				}
				map[i] = j;
			}
			log.Add("bones: " + string.Join(", ", mapLog));
			Matrix4x4[] fit = null;
			if (fitLimbs)
			{
				var mapped = blPos.Select(MapPoint).ToArray();
				fit = ComputeLimbFit(blBones, m2Bones, mapped, m2Pos, log);
				if (worstFitName != null)
					log.Add($"largest joint offset before fitting {worstFit * 100:0.0} cm ({worstFitName})");
			}
			else if (worstFitName != null)
			{
				log.Add($"largest joint offset {worstFit * 100:0.0} cm ({worstFitName})");
			}

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
					Remap(data, map, m2Bones.Count, MapPoint, MapDirection, fit, mirror);
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
		/// Per bannerlord bone, a model-space transform (in the mapped space) that puts the limbs onto the m2
		/// joints: each arm / leg segment is rotated, moved and stretched along its length so both of its
		/// joints land on the m2 joints; hands and feet keep the rotation of their segment and move onto the
		/// m2 joint; the clavicle keeps its root on the torso and swings / stretches so its tip meets the m2
		/// shoulder. Twist / toe / finger bones follow their segment. The torso stays where it is.
		/// </summary>
		private static Matrix4x4[] ComputeLimbFit(List<BoneNode> bl, List<BoneNode> m2, Vector3[] p, Vector3[] q,
			List<string> log)
		{
			var fit = Enumerable.Repeat(Matrix4x4.Identity, bl.Count).ToArray();
			int B(string n) => IndexOf(bl, n);
			int M(string n) => IndexOf(m2, n);
			float maxMove = 0;
			var fitted = new List<string>();

			// segment a->b onto m2 joints ma->mb; returns the rotation used
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

			foreach (var side in new[] { "l", "r" })
			{
				var S = side.ToUpperInvariant();

				// arm
				var upper = B(side + "_upperarm_twist");
				var fore = B(side + "_foretwist");
				var hand = B(side + "_hand");
				var mUpper = M("bone_" + S + "upperarm");
				var rUpper = Segment(upper, fore, mUpper, M("bone_" + S + "elbow"));
				var rFore = Segment(fore, hand, M("bone_" + S + "elbow"), M("bone_" + S + "hand"));
				if (rUpper.HasValue && rFore.HasValue)
				{
					End(hand, M("bone_" + S + "hand"), rFore);
					Follow(B(side + "_upperarm_twist1"), upper);
					Follow(B(side + "_foretwist1"), fore);
					Follow(B(side + "_finger0"), hand);

					// clavicle: root stays on the torso, tip goes to the m2 shoulder
					var clavicle = B(side + "_clavicle");
					if (clavicle >= 0 && upper >= 0 && mUpper >= 0)
					{
						var from = p[upper] - p[clavicle];
						var to = q[mUpper] - p[clavicle];
						if (from.LengthSquared() > 1e-10f && to.LengthSquared() > 1e-10f)
						{
							var k = Math.Max(0.75f, Math.Min(1.33f, to.Length() / from.Length()));
							fit[clavicle] = Matrix4x4.CreateTranslation(-p[clavicle]) *
							                Matrix4x4.CreateFromQuaternion(FromTo(Vector3.Normalize(from), Vector3.Normalize(to))) *
							                AxisScale(Vector3.Normalize(to), k) * Matrix4x4.CreateTranslation(p[clavicle]);
						}
					}
					fitted.Add(side + " arm");
				}

				// leg
				var thigh = B(side + "_thigh");
				var calf = B(side + "_calf");
				var foot = B(side + "_foot");
				var rThigh = Segment(thigh, calf, M("bone_" + S + "Thigh"), M("bone_" + S + "lowerleg"));
				var rCalf = Segment(calf, foot, M("bone_" + S + "lowerleg"), M("bone_" + S + "foot"));
				if (rThigh.HasValue && rCalf.HasValue)
				{
					// keep the foot flat: move it, don't tilt it with the shin
					End(foot, M("bone_" + S + "foot"), null);
					Follow(B(side + "_toe0"), foot);
					fitted.Add(side + " leg");
				}
			}

			if (fitted.Count > 0)
				log.Add($"fitted {string.Join(", ", fitted)} onto the m2 joints (moved up to {maxMove * 100:0.0} cm)");
			return fit;
		}

		private static Matrix4x4 AxisScale(Vector3 axis, float k)
		{
			// scale by k along a unit axis: I + (k - 1) * axis * axis^T (symmetric, so row/column order is moot)
			var f = k - 1f;
			return new Matrix4x4(
				1 + f * axis.X * axis.X, f * axis.X * axis.Y, f * axis.X * axis.Z, 0,
				f * axis.Y * axis.X, 1 + f * axis.Y * axis.Y, f * axis.Y * axis.Z, 0,
				f * axis.Z * axis.X, f * axis.Z * axis.Y, 1 + f * axis.Z * axis.Z, 0,
				0, 0, 0, 1);
		}

		private static Quaternion FromTo(Vector3 from, Vector3 to)
		{
			var dot = Vector3.Dot(from, to);
			if (dot > 0.999999f)
				return Quaternion.Identity;
			if (dot < -0.999999f)
			{
				var axis = Vector3.Cross(Vector3.UnitX, from);
				if (axis.LengthSquared() < 1e-6f)
					axis = Vector3.Cross(Vector3.UnitY, from);
				return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), (float) Math.PI);
			}
			var cross = Vector3.Cross(from, to);
			return Quaternion.Normalize(new Quaternion(cross.X, cross.Y, cross.Z, 1f + dot));
		}

		private static bool IsMergedBone(string name)
		{
			var n = name.ToLowerInvariant();
			return n == "spine" || n == "neck" || n.EndsWith("twist1") || n.EndsWith("foretwist1") ||
			       n.EndsWith("finger0") || n.EndsWith("toe0");
		}

		private static string MapName(string blName)
		{
			var n = blName.ToLowerInvariant();
			if (BoneMap.TryGetValue(n, out var direct))
				return direct;
			if ((n.StartsWith("l_") || n.StartsWith("r_")) && BoneMap.TryGetValue("{s}" + n.Substring(1), out var sided))
				return sided.Replace("{S}", n[0] == 'l' ? "L" : "R");
			return null;
		}

		private static int IndexOf(List<BoneNode> bones, string name)
		{
			for (int i = 0; i < bones.Count; i++)
				if (string.Equals(bones[i].Name, name, StringComparison.OrdinalIgnoreCase))
					return i;
			return -1;
		}

		private static Matrix4x4[] Globals(List<BoneNode> bones, bool ignoreScale)
		{
			var result = new Matrix4x4[bones.Count];
			var ids = new Dictionary<BoneNode, int>();
			for (int i = 0; i < bones.Count; i++)
				ids[bones[i]] = i;
			var done = new bool[bones.Count];
			Matrix4x4 Get(int i)
			{
				if (done[i])
					return result[i];
				var local = bones[i].RestFrame;
				if (ignoreScale)
					local.M44 = 1f;
				var parent = bones[i].Parent;
				result[i] = parent != null && ids.TryGetValue(parent, out var p) ? local * Get(p) : local;
				done[i] = true;
				return result[i];
			}
			for (int i = 0; i < bones.Count; i++)
				Get(i);
			return result;
		}

#pragma warning disable 612
		private static void Remap(MeshEditData data, int[] map, int m2BoneCount,
			Func<Vector3, Vector3> mapPoint, Func<Vector3, Vector3> mapDirection, Matrix4x4[] fit, bool mirror)
		{
			// limb fitting skins the mesh with the original (bannerlord) weights, after the space change
			var original = data.Bones;
			Matrix4x4? FitMatrix(int positionIndex)
			{
				if (fit == null || original == null || positionIndex < 0 || positionIndex >= original.Length)
					return null;
				var b = original[positionIndex];
				var m = new Matrix4x4();
				float total = 0;
				bool moved = false;
				void Acc(byte bone, float w)
				{
					if (w <= 0 || bone >= fit.Length)
						return;
					m += fit[bone] * w;
					total += w;
					moved |= !fit[bone].IsIdentity;
				}
				Acc(b.B0, b.W0); Acc(b.B1, b.W1); Acc(b.B2, b.W2); Acc(b.B3, b.W3);
				if (!moved || total <= 1e-6f)
					return null;
				return m * (1f / total);
			}
			Vector3 FitPoint(Vector3 p, int positionIndex)
			{
				var m = FitMatrix(positionIndex);
				return m.HasValue ? Vector3.Transform(p, m.Value) : p;
			}
			Vector3 FitDirection(Vector3 d, int positionIndex)
			{
				var m = FitMatrix(positionIndex);
				if (!m.HasValue)
					return d;
				var r = Vector3.TransformNormal(d, m.Value);
				return r.LengthSquared() > 1e-12f ? Vector3.Normalize(r) : d;
			}

			if (data.Positions != null)
			{
				data.Positions = data.Positions.Select((p, i) =>
					new Vector4(FitPoint(mapPoint(new Vector3(p.X, p.Y, p.Z)), i), p.W)).ToArray();
			}

			if (data.Vertices != null)
			{
				var vertices = (MeshEditData.Vertex[]) data.Vertices.Clone();
				for (int i = 0; i < vertices.Length; i++)
				{
					var pi = (int) vertices[i].PositionIndex;
					Func<Vector3, Vector3> dir = d => FitDirection(mapDirection(d), pi);
					vertices[i].Normal = MapDir4(vertices[i].Normal, dir);
					vertices[i].Tangent = MapDir4(vertices[i].Tangent, dir);
					vertices[i].Binormal = MapDir4(vertices[i].Binormal, dir);
				}
				data.Vertices = vertices;
			}

			// a mirror turns every triangle inside out: reverse the winding
			if (mirror && data.Faces != null)
			{
				data.Faces = data.Faces.Select(f => new MeshEditData.Face { V0 = f.V0, V1 = f.V2, V2 = f.V1 }).ToArray();
			}

			if (data.MorphFrames != null)
			{
				foreach (var frame in data.MorphFrames)
				{
					if (frame.Positions != null)
						frame.Positions = frame.Positions.Select((p, i) =>
							new Vector4(FitPoint(mapPoint(new Vector3(p.X, p.Y, p.Z)), i), p.W)).ToArray();
					if (frame.Normals != null)
					{
						var verts = data.Vertices;
						frame.Normals = frame.Normals.Select((n, i) =>
						{
							var pi = verts != null && i < verts.Length ? (int) verts[i].PositionIndex : -1;
							return MapDir4(n, d => FitDirection(mapDirection(d), pi));
						}).ToArray();
					}
				}
			}

			if (data.Bones != null)
			{
				var weights = new MeshEditData.Bone[data.Bones.Length];
				var sums = new Dictionary<int, float>();
				for (int i = 0; i < weights.Length; i++)
				{
					sums.Clear();
					var b = data.Bones[i];
					Add(sums, map, b.B0, b.W0);
					Add(sums, map, b.B1, b.W1);
					Add(sums, map, b.B2, b.W2);
					Add(sums, map, b.B3, b.W3);
					var top = sums.Where(kv => kv.Key < m2BoneCount && kv.Value > 0)
						.OrderByDescending(kv => kv.Value).Take(4).ToArray();
					var result = new MeshEditData.Bone();
					if (top.Length > 0) { result.B0 = (byte) top[0].Key; result.W0 = top[0].Value; }
					if (top.Length > 1) { result.B1 = (byte) top[1].Key; result.W1 = top[1].Value; }
					if (top.Length > 2) { result.B2 = (byte) top[2].Key; result.W2 = top[2].Value; }
					if (top.Length > 3) { result.B3 = (byte) top[3].Key; result.W3 = top[3].Value; }
					weights[i] = result;
				}
				data.Bones = weights;
			}
		}

		private static void Add(Dictionary<int, float> sums, int[] map, byte bone, float weight)
		{
			if (weight <= 0f || bone >= map.Length)
				return;
			var target = map[bone];
			sums.TryGetValue(target, out var current);
			sums[target] = current + weight;
		}

		private static Vector4 MapDir4(Vector4 v, Func<Vector3, Vector3> mapDirection)
		{
			var d = mapDirection(new Vector3(v.X, v.Y, v.Z));
			return new Vector4(d, v.W);
		}

		private sealed class RestoreScope : IDisposable
		{
			private readonly List<Action> _restores = new List<Action>();
			private readonly List<object> _keepAlive = new List<object>();

			public void Save(MeshEditData data)
			{
				// strong reference: the loader only holds the data weakly and would reload the original
				_keepAlive.Add(data);
				var positions = data.Positions;
				var vertices = data.Vertices;
				var faces = data.Faces;
				var bones = data.Bones;
				var frames = data.MorphFrames?.Select(f => Tuple.Create(f, f.Positions, f.Normals)).ToList();
				_restores.Add(() =>
				{
					data.Positions = positions;
					data.Vertices = vertices;
					data.Faces = faces;
					data.Bones = bones;
					if (frames != null)
						foreach (var frame in frames)
						{
							frame.Item1.Positions = frame.Item2;
							frame.Item1.Normals = frame.Item3;
						}
				});
			}

			public void Dispose()
			{
				foreach (var restore in _restores)
					restore();
				_restores.Clear();
				_keepAlive.Clear();
			}
		}
#pragma warning restore 612

		#endregion
	}
}
