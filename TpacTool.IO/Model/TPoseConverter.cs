using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using TpacTool.Lib;

namespace TpacTool.IO
{
	/// <summary>
	/// Re-poses a skeleton from its authored rest pose (Bannerlord's human skeleton is an A-pose)
	/// into a T-pose: each upper arm is rotated so it points straight out sideways, and optionally
	/// each forearm is straightened so the elbow is not bent. Skinned meshes are deformed with
	/// linear blend skinning so they still match the new bind pose.
	/// <para/>
	/// <see cref="Apply"/> mutates the skeleton rest frames and mesh vertex data in place and returns
	/// a scope that restores the original data when disposed, so every exporter (fbx, dae, obj)
	/// can export the posed data without knowing about it.
	/// </summary>
	public sealed class TPoseConverter
	{
		private static readonly Regex SplitRegex = new Regex("[^a-z0-9]+", RegexOptions.Compiled);

		// e.g. "upperarml", "forearm_r" after the separators were removed, or "handl"
		private static readonly Regex SuffixSideRegex =
			new Regex("(arm|hand|wrist|shoulder|clavicle|elbow)(l|r)$", RegexOptions.Compiled);

		private readonly List<BoneNode> _bones;
		private readonly Dictionary<BoneNode, int> _boneIds = new Dictionary<BoneNode, int>();
		private readonly bool _ignoreScale;
		private readonly Matrix4x4[] _globals;
		private readonly Matrix4x4[] _deltas;

		/// <summary>Per-bone model-space delta: newGlobal = oldGlobal * Delta. Also the skinning matrix.</summary>
		public Matrix4x4[] Deltas => _deltas;

		/// <summary>New local rest frames, in the same convention as <see cref="BoneNode.RestFrame"/>.</summary>
		public Matrix4x4[] NewRestFrames { get; }

		/// <summary>Names of bones whose orientation was changed.</summary>
		public List<string> AdjustedBones { get; } = new List<string>();

		/// <summary>Human-readable notes (bones found, angles applied, or why nothing was changed).</summary>
		public List<string> Log { get; } = new List<string>();

		public bool HasChanges => AdjustedBones.Count > 0;

		private TPoseConverter(SkeletonDefinitionData skeleton, bool ignoreScale)
		{
			_bones = skeleton.Bones;
			_ignoreScale = ignoreScale;
			for (int i = 0; i < _bones.Count; i++)
				_boneIds[_bones[i]] = i;
			_globals = new Matrix4x4[_bones.Count];
			_deltas = new Matrix4x4[_bones.Count];
			NewRestFrames = new Matrix4x4[_bones.Count];
			var computed = new bool[_bones.Count];
			for (int i = 0; i < _bones.Count; i++)
			{
				_deltas[i] = Matrix4x4.Identity;
				ComputeGlobal(i, computed);
			}
		}

		public static bool IsIgnoreScaleSkeleton([NotNull] Skeleton skeleton)
		{
			return skeleton.UserData?.Data != null &&
			       (skeleton.UserData.Data.Usage == SkeletonUserData.USAGE_HUMAN ||
			        skeleton.UserData.Data.Usage == SkeletonUserData.USAGE_HORSE);
		}

		/// <summary>
		/// Works out the T-pose for a skeleton without changing anything.
		/// </summary>
		public static TPoseConverter Compute([NotNull] Skeleton skeleton, bool straightenElbows = true)
		{
			var data = skeleton.Definition?.Data;
			if (data == null || data.Bones.Count == 0)
				throw new ArgumentException("Skeleton " + skeleton.Name + " has no bone data");
			var converter = new TPoseConverter(data, IsIgnoreScaleSkeleton(skeleton));
			converter.Solve(straightenElbows);
			return converter;
		}

		/// <summary>
		/// Poses the skeleton (and the skinned meshes) in T-pose. Dispose the returned scope to restore
		/// the original data. Returns a no-op scope when no arm bones could be identified.
		/// </summary>
		public static TPoseScope Apply([NotNull] Skeleton skeleton, [CanBeNull] IEnumerable<Mesh> meshes,
			bool straightenElbows = true)
		{
			var converter = Compute(skeleton, straightenElbows);
			var scope = new TPoseScope(converter);
			if (!converter.HasChanges)
				return scope;

			try
			{
				// external data is only weakly referenced by its loader: if it were collected mid-export
				// the exporter would silently reload the original A-pose data from disk
				var definition = skeleton.Definition.Data;
				scope.KeepAlive(definition);
				var bones = definition.Bones;
				for (int i = 0; i < bones.Count; i++)
				{
					scope.SaveBone(bones[i]);
					bones[i].RestFrame = converter.NewRestFrames[i];
				}

				if (meshes != null)
				{
					var visited = new HashSet<MeshEditData>();
					foreach (var mesh in meshes)
					{
						var editData = mesh?.EditData?.Data;
						if (editData == null || !visited.Add(editData))
							continue;
						converter.DeformMesh(editData, scope);
					}
				}
			}
			catch
			{
				scope.Dispose();
				throw;
			}

			return scope;
		}

		#region Solver

		private void ComputeGlobal(int i, bool[] computed)
		{
			if (computed[i])
				return;
			var local = GetLocal(i);
			var parent = _bones[i].Parent;
			if (parent != null && _boneIds.TryGetValue(parent, out var parentId))
			{
				ComputeGlobal(parentId, computed);
				_globals[i] = local * _globals[parentId];
			}
			else
			{
				_globals[i] = local;
			}
			computed[i] = true;
		}

		private Matrix4x4 GetLocal(int i)
		{
			var matrix = _bones[i].RestFrame;
			if (_ignoreScale)
				matrix.M44 = 1f;
			return matrix;
		}

		private Vector3 GetPosition(int i)
		{
			return (_globals[i] * _deltas[i]).Translation;
		}

		private void Solve(bool straightenElbows)
		{
			var names = _bones.Select(b => (b.Name ?? string.Empty).ToLowerInvariant()).ToArray();

			var upperL = FindBone(names, 1, BoneRole.UpperArm);
			var upperR = FindBone(names, -1, BoneRole.UpperArm);
			if (upperL < 0 && upperR < 0)
			{
				Log.Add("No upper arm bones found (looked for names like upperarm_l / upperarm_r); skeleton left unchanged.");
				FinishRestFrames();
				return;
			}

			var up = GuessUpAxis(names);

			Vector3 outwardL, outwardR;
			if (upperL >= 0 && upperR >= 0)
			{
				var lateral = Horizontal(GetPosition(upperL) - GetPosition(upperR), up);
				if (lateral.LengthSquared() < 1e-10f)
					lateral = Vector3.UnitX;
				lateral = Vector3.Normalize(lateral);
				outwardL = lateral;
				outwardR = -lateral;
			}
			else
			{
				var center = GetPosition(FindCenterBone(names));
				var side = upperL >= 0 ? upperL : upperR;
				var o = Horizontal(GetPosition(side) - center, up);
				if (o.LengthSquared() < 1e-10f)
					o = Vector3.UnitX;
				o = Vector3.Normalize(o);
				outwardL = o;
				outwardR = o; // only one side is used
			}

			if (upperL >= 0)
				SolveArm(names, upperL, 1, outwardL, straightenElbows);
			if (upperR >= 0)
				SolveArm(names, upperR, -1, outwardR, straightenElbows);

			FinishRestFrames();
		}

		private void SolveArm(string[] names, int upper, int side, Vector3 outward, bool straightenElbows)
		{
			var sideName = side > 0 ? "left" : "right";
			var fore = FindBone(names, side, BoneRole.ForeArm, upper);
			if (fore < 0)
				fore = FindLongestChild(upper, names);
			if (fore < 0)
			{
				Log.Add($"{sideName} arm: no forearm under '{_bones[upper].Name}', skipped.");
				return;
			}

			// 1. swing the whole arm about the shoulder joint so upper arm -> forearm points outward
			var upperSet = CollectArmSet(upper, names, side, BoneRole.UpperArm);
			var angle1 = RotateGroup(upper, fore, outward, upperSet);
			Log.Add($"{sideName} arm: rotated '{_bones[upper].Name}' by {angle1:0.#}°");

			if (!straightenElbows)
				return;

			// 2. straighten the elbow so forearm -> hand also points outward
			var hand = FindBone(names, side, BoneRole.Hand, fore);
			if (hand < 0)
				hand = FindLongestChild(fore, names);
			if (hand < 0)
				return;
			var foreSet = CollectArmSet(fore, names, side, BoneRole.ForeArm);
			var angle2 = RotateGroup(fore, hand, outward, foreSet);
			Log.Add($"{sideName} arm: straightened '{_bones[fore].Name}' by {angle2:0.#}°");
		}

		/// <summary>
		/// Rotates every bone in <paramref name="group"/> about the joint at <paramref name="pivotBone"/>
		/// so the pivot -> target bone direction becomes <paramref name="direction"/>.
		/// </summary>
		private float RotateGroup(int pivotBone, int targetBone, Vector3 direction, HashSet<int> group)
		{
			var pivot = GetPosition(pivotBone);
			var current = GetPosition(targetBone) - pivot;
			if (current.LengthSquared() < 1e-12f)
				return 0f;
			current = Vector3.Normalize(current);
			var rotation = FromToRotation(current, direction);
			var angle = (float) (Math.Acos(Math.Max(-1f, Math.Min(1f, Vector3.Dot(current, direction)))) * 180.0 / Math.PI);
			if (angle < 0.01f)
				return 0f;

			// row-vector convention: x' = (x - pivot) * R + pivot
			var m = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromQuaternion(rotation) *
			        Matrix4x4.CreateTranslation(pivot);
			foreach (var i in group)
			{
				_deltas[i] = _deltas[i] * m;
				if (!AdjustedBones.Contains(_bones[i].Name))
					AdjustedBones.Add(_bones[i].Name);
			}
			return angle;
		}

		private void FinishRestFrames()
		{
			for (int i = 0; i < _bones.Count; i++)
			{
				var original = _bones[i].RestFrame;
				var parent = _bones[i].Parent;
				int parentId = -1;
				if (parent != null && !_boneIds.TryGetValue(parent, out parentId))
					parentId = -1;
				var parentDelta = parentId >= 0 ? _deltas[parentId] : Matrix4x4.Identity;
				if (_deltas[i] == parentDelta)
				{
					// moved rigidly with its parent (or not at all): local frame is unchanged
					NewRestFrames[i] = original;
					continue;
				}

				var newGlobal = _globals[i] * _deltas[i];
				Matrix4x4 local;
				if (parentId >= 0)
				{
					Matrix4x4.Invert(_globals[parentId] * _deltas[parentId], out var invParent);
					local = newGlobal * invParent;
				}
				else
				{
					local = newGlobal;
				}

				if (_ignoreScale)
					local.M44 = original.M44;
				NewRestFrames[i] = local;
			}
		}

		private static Quaternion FromToRotation(Vector3 from, Vector3 to)
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
			var q = new Quaternion(cross.X, cross.Y, cross.Z, 1f + dot);
			return Quaternion.Normalize(q);
		}

		private static Vector3 Horizontal(Vector3 v, Vector3 up)
		{
			return v - Vector3.Dot(v, up) * up;
		}

		private Vector3 GuessUpAxis(string[] names)
		{
			int head = -1;
			for (int i = 0; i < names.Length && head < 0; i++)
				if (HasToken(names[i], "head") || names[i] == "head")
					head = i;
			for (int i = 0; i < names.Length && head < 0; i++)
				if (names[i].Contains("neck"))
					head = i;

			if (head >= 0)
			{
				var v = GetPosition(head) - GetPosition(FindCenterBone(names, head));
				if (v.LengthSquared() > 1e-8f)
				{
					// snap to the dominant axis: skeletons are authored axis-aligned
					var ax = Math.Abs(v.X);
					var ay = Math.Abs(v.Y);
					var az = Math.Abs(v.Z);
					if (az >= ax && az >= ay)
						return new Vector3(0, 0, Math.Sign(v.Z));
					if (ay >= ax)
						return new Vector3(0, Math.Sign(v.Y), 0);
					return new Vector3(Math.Sign(v.X), 0, 0);
				}
			}

			// bannerlord is z-up
			return Vector3.UnitZ;
		}

		private int FindCenterBone(string[] names, int exclude = -1)
		{
			string[] candidates = { "pelvis", "hips", "hip", "abdomen", "spine", "root" };
			foreach (var candidate in candidates)
			{
				for (int i = 0; i < names.Length; i++)
					if (i != exclude && names[i].Contains(candidate) && GetSide(names[i]) == 0)
						return i;
			}
			return 0;
		}

		#endregion

		#region Bone identification

		private enum BoneRole
		{
			UpperArm,
			ForeArm,
			Hand
		}

		private static bool IsTwist(string name)
		{
			return name.Contains("twist") || name.Contains("roll") || name.Contains("helper") ||
			       name.Contains("corrective") || name.Contains("ik");
		}

		private static bool MatchesRole(string name, BoneRole role, bool allowTwist)
		{
			if (!allowTwist && IsTwist(name))
				return false;
			switch (role)
			{
				case BoneRole.UpperArm:
					if (name.Contains("upperarm") || name.Contains("upper_arm") || name.Contains("uparm") ||
					    name.Contains("humerus"))
						return true;
					// plain "arm_l" / "LeftArm" (mixamo) style names
					var stripped = StripSide(name).Replace("left", "").Replace("right", "").Replace("_", "");
					return stripped == "arm";
				case BoneRole.ForeArm:
					// bannerlord's human skeleton names its forearm "l_foretwist"
					return name.Contains("forearm") || name.Contains("fore_arm") || name.Contains("foretwist") ||
					       name.Contains("fore_twist") || name.Contains("lowerarm") || name.Contains("lower_arm") ||
					       name.Contains("elbow");
				case BoneRole.Hand:
					return (name.Contains("hand") || name.Contains("wrist")) && !name.Contains("finger") &&
					       !name.Contains("thumb") && !name.Contains("item");
			}
			return false;
		}

		private static bool HasToken(string name, string token)
		{
			return SplitRegex.Split(name).Contains(token);
		}

		private static string StripSide(string name)
		{
			var tokens = SplitRegex.Split(name)
				.Where(t => t.Length > 0 && t != "l" && t != "r" && t != "left" && t != "right");
			return string.Join("_", tokens);
		}

		/// <returns>1 for left, -1 for right, 0 for center / unknown</returns>
		private static int GetSide(string name)
		{
			var tokens = SplitRegex.Split(name);
			if (tokens.Contains("l") || tokens.Contains("left") || tokens.Contains("lft"))
				return 1;
			if (tokens.Contains("r") || tokens.Contains("right") || tokens.Contains("rgt"))
				return -1;
			if (name.Contains("left"))
				return 1;
			if (name.Contains("right"))
				return -1;
			var compact = name.Replace("_", "").Replace(".", "").Replace(" ", "");
			var match = SuffixSideRegex.Match(compact);
			if (match.Success)
				return match.Groups[2].Value == "l" ? 1 : -1;
			return 0;
		}

		/// <summary>
		/// Finds the bone with the given role and side. When <paramref name="under"/> is given, the result
		/// must be a descendant of that bone.
		/// </summary>
		private int FindBone(string[] names, int side, BoneRole role, int under = -1)
		{
			// bannerlord's human skeleton has no plain upper arm / forearm: the chain is
			// l_clavicle > l_upperarm_twist > l_upperarm_twist1 > l_foretwist > l_foretwist1 > l_hand,
			// so when no regular bone matches, the topmost twist-named bone of the segment is the joint
			var result = FindBone(names, side, role, under, false);
			return result >= 0 ? result : FindBone(names, side, role, under, true);
		}

		private int FindBone(string[] names, int side, BoneRole role, int under, bool allowTwist)
		{
			int best = -1;
			int bestDepth = int.MaxValue;
			for (int i = 0; i < names.Length; i++)
			{
				if (GetSide(names[i]) != side || !MatchesRole(names[i], role, allowTwist))
					continue;
				if (under >= 0 && !IsDescendant(i, under))
					continue;
				// prefer the bone closest to the root (e.g. "upperarm_l" over a nested "upperarm_l_end")
				var depth = GetDepth(i);
				if (depth < bestDepth)
				{
					best = i;
					bestDepth = depth;
				}
			}
			return best;
		}

		private int FindLongestChild(int bone, string[] names)
		{
			int best = -1;
			float bestLen = 0f;
			var pos = GetPosition(bone);
			for (int i = 0; i < _bones.Count; i++)
			{
				if (_bones[i].Parent != _bones[bone] || IsTwist(names[i]))
					continue;
				var len = (GetPosition(i) - pos).LengthSquared();
				if (len > bestLen)
				{
					best = i;
					bestLen = len;
				}
			}
			return bestLen > 1e-10f ? best : -1;
		}

		private bool IsDescendant(int bone, int ancestor)
		{
			var target = _bones[ancestor];
			for (var p = _bones[bone].Parent; p != null; p = p.Parent)
				if (p == target)
					return true;
			return false;
		}

		private int GetDepth(int bone)
		{
			int depth = 0;
			for (var p = _bones[bone].Parent; p != null; p = p.Parent)
				depth++;
			return depth;
		}

		/// <summary>
		/// The bone, all its descendants, plus same-side twist/helper bones of the same segment that are
		/// parented elsewhere (e.g. an upper arm twist bone parented to the clavicle).
		/// </summary>
		private HashSet<int> CollectArmSet(int root, string[] names, int side, BoneRole role)
		{
			var set = new HashSet<int>();
			for (int i = 0; i < _bones.Count; i++)
			{
				if (i == root || IsDescendant(i, root))
				{
					set.Add(i);
					continue;
				}
				if (IsDescendant(root, i))
					continue; // never move an ancestor (clavicle, spine...)
				var n = names[i];
				if (GetSide(n) != side || !IsTwist(n))
					continue;
				bool sameSegment = role == BoneRole.UpperArm
					? (n.Contains("upperarm") || n.Contains("upper_arm") || n.Contains("uparm"))
					: (n.Contains("forearm") || n.Contains("foretwist") || n.Contains("lowerarm") || n.Contains("lower_arm"));
				if (sameSegment)
				{
					set.Add(i);
					for (int j = 0; j < _bones.Count; j++)
						if (IsDescendant(j, i))
							set.Add(j);
				}
			}
			return set;
		}

		#endregion

		#region Mesh deformation

		private void DeformMesh(MeshEditData data, TPoseScope scope)
		{
#pragma warning disable 612
			var weights = data.Bones;
#pragma warning restore 612
			if (weights == null || weights.Length == 0 || data.Positions == null)
				return;

			var positions = (Vector4[]) data.Positions.Clone();
			for (int i = 0; i < positions.Length && i < weights.Length; i++)
			{
				if (TryBlend(weights[i], out var skin))
				{
					var p = positions[i];
					var moved = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), skin);
					positions[i] = new Vector4(moved, p.W);
				}
			}

			MeshEditData.Vertex[] vertices = null;
			if (data.Vertices != null)
			{
				vertices = (MeshEditData.Vertex[]) data.Vertices.Clone();
				for (int i = 0; i < vertices.Length; i++)
				{
					var posIndex = (int) vertices[i].PositionIndex;
					if (posIndex >= weights.Length || !TryBlend(weights[posIndex], out var skin))
						continue;
					vertices[i].Normal = TransformDirection(vertices[i].Normal, skin);
					vertices[i].Tangent = TransformDirection(vertices[i].Tangent, skin);
					vertices[i].Binormal = TransformDirection(vertices[i].Binormal, skin);
				}
			}

			var morphPositions = new List<Vector4[]>();
			var morphNormals = new List<Vector4[]>();
			if (data.MorphFrames != null)
			{
				foreach (var frame in data.MorphFrames)
				{
					Vector4[] framePositions = null;
					if (frame.Positions != null)
					{
						framePositions = (Vector4[]) frame.Positions.Clone();
						for (int i = 0; i < framePositions.Length && i < weights.Length; i++)
						{
							if (TryBlend(weights[i], out var skin))
							{
								var p = framePositions[i];
								framePositions[i] = new Vector4(Vector3.Transform(new Vector3(p.X, p.Y, p.Z), skin), p.W);
							}
						}
					}

					Vector4[] frameNormals = null;
					if (frame.Normals != null && data.Vertices != null)
					{
						frameNormals = (Vector4[]) frame.Normals.Clone();
						for (int i = 0; i < frameNormals.Length && i < data.Vertices.Length; i++)
						{
							var posIndex = (int) data.Vertices[i].PositionIndex;
							if (posIndex < weights.Length && TryBlend(weights[posIndex], out var skin))
								frameNormals[i] = TransformDirection(frameNormals[i], skin);
						}
					}

					morphPositions.Add(framePositions);
					morphNormals.Add(frameNormals);
				}
			}

			scope.SaveMesh(data);
			data.Positions = positions;
			if (vertices != null)
				data.Vertices = vertices;
			if (data.MorphFrames != null)
			{
				for (int i = 0; i < data.MorphFrames.Count; i++)
				{
					if (morphPositions[i] != null)
						data.MorphFrames[i].Positions = morphPositions[i];
					if (morphNormals[i] != null)
						data.MorphFrames[i].Normals = morphNormals[i];
				}
			}
		}

		private bool TryBlend(MeshEditData.Bone weight, out Matrix4x4 skin)
		{
			skin = new Matrix4x4();
			float total = 0f;
			bool any = false;
			Accumulate(ref skin, ref total, ref any, weight.B0, weight.W0);
			Accumulate(ref skin, ref total, ref any, weight.B1, weight.W1);
			Accumulate(ref skin, ref total, ref any, weight.B2, weight.W2);
			Accumulate(ref skin, ref total, ref any, weight.B3, weight.W3);
			if (!any || total <= 1e-6f)
				return false;
			skin = skin * (1f / total);
			return true;
		}

		private void Accumulate(ref Matrix4x4 skin, ref float total, ref bool moved, byte bone, float weight)
		{
			if (weight <= 0f || bone >= _deltas.Length)
				return;
			var delta = _deltas[bone];
			if (!delta.IsIdentity)
				moved = true;
			skin += delta * weight;
			total += weight;
		}

		private static Vector4 TransformDirection(Vector4 v, Matrix4x4 skin)
		{
			var d = Vector3.TransformNormal(new Vector3(v.X, v.Y, v.Z), skin);
			if (d.LengthSquared() > 1e-12f)
				d = Vector3.Normalize(d);
			return new Vector4(d, v.W);
		}

		#endregion
	}

	/// <summary>
	/// Restores skeleton rest frames and mesh vertex data changed by <see cref="TPoseConverter.Apply"/>.
	/// </summary>
	public sealed class TPoseScope : IDisposable
	{
		private readonly List<KeyValuePair<BoneNode, Matrix4x4>> _bones = new List<KeyValuePair<BoneNode, Matrix4x4>>();
		private readonly List<Action> _restores = new List<Action>();
		private readonly List<object> _keepAlive = new List<object>();
		private bool _disposed;

		public TPoseConverter Converter { get; }

		internal TPoseScope(TPoseConverter converter)
		{
			Converter = converter;
		}

		internal void SaveBone(BoneNode bone)
		{
			_bones.Add(new KeyValuePair<BoneNode, Matrix4x4>(bone, bone.RestFrame));
		}

		internal void KeepAlive(object data)
		{
			_keepAlive.Add(data);
		}

		internal void SaveMesh(MeshEditData data)
		{
			_keepAlive.Add(data);
			var positions = data.Positions;
			var vertices = data.Vertices;
			var frames = data.MorphFrames?.Select(f => Tuple.Create(f, f.Positions, f.Normals)).ToList();
			_restores.Add(() =>
			{
				data.Positions = positions;
				data.Vertices = vertices;
				if (frames != null)
				{
					foreach (var frame in frames)
					{
						frame.Item1.Positions = frame.Item2;
						frame.Item1.Normals = frame.Item3;
					}
				}
			});
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;
			foreach (var pair in _bones)
				pair.Key.RestFrame = pair.Value;
			foreach (var restore in _restores)
				restore();
			_keepAlive.Clear();
		}
	}
}
