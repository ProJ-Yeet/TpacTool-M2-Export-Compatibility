using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using TpacTool.IO;
using TpacTool.IO.Assimp;
using TpacTool.Lib;
using Material = TpacTool.Lib.Material;

namespace TpacTool
{
	public enum GallerySkeletonMode
	{
		/// <summary>Rigged models use the human skeleton (horse skeleton for horse/camel gear).</summary>
		Auto,
		/// <summary>Export every model as a static mesh.</summary>
		None,
		/// <summary>Rigged models use <see cref="GalleryExportSettings.Skeleton"/>.</summary>
		Specific
	}

	/// <summary>How the selected models are spread over files.</summary>
	public enum GalleryGrouping
	{
		/// <summary>One file per model.</summary>
		PerModel,
		/// <summary>All models in one file named <see cref="GalleryExportSettings.CombinedName"/>.</summary>
		Combined,
		/// <summary>
		/// One file per texture set: models that use the same textures share a file named after them
		/// (a model using two textures goes to "tex1+tex2"). Models are not split.
		/// </summary>
		ByTexture,
		/// <summary>
		/// One file per texture: every mesh goes to the file of its texture, so a model using two textures
		/// is split between those two files.
		/// </summary>
		SplitByTexture
	}

	/// <summary>One file to write: <see cref="Run"/> exports it and returns a short note for the log.</summary>
	public sealed class GalleryExportJob
	{
		public string Name;
		public Func<string> Run;
	}

	public sealed class GalleryExportSettings
	{
		public string OutputDir;
		public string ModelExtension = "fbx"; // fbx, obj or dae
		public int TexturePreference; // 0 = best for the format, 1 = png, 2 = dds
		public bool OneFolderPerAsset = true;
		public GallerySkeletonMode SkeletonMode = GallerySkeletonMode.Auto;
		public Skeleton Skeleton;
		public Skeleton HumanSkeleton;
		public Skeleton HorseSkeleton;
		public bool ConvertToTPose;
		public bool StraightenElbows = true;
		/// <summary>The LODs to export. A model without one of them uses its nearest lower LOD.</summary>
		public List<int> Lods = new List<int> { 0 };
		/// <summary>With several LODs: each LOD goes to its own file (name_lod1, ...) instead of all into one.</summary>
		public bool SeparateLodFiles = true;
		public GalleryGrouping Grouping = GalleryGrouping.PerModel;
		/// <summary>The file name for <see cref="GalleryGrouping.Combined"/>.</summary>
		public string CombinedName = "combined";
		/// <summary>
		/// When several models / meshes share a file (every grouping but <see cref="GalleryGrouping.PerModel"/>):
		/// leave out connected mesh parts that are byte identical to a part already in the file.
		/// </summary>
		public bool RemoveDuplicateParts = true;
		public bool DiffuseOnly;
		public bool LargerScale;
		public bool NegYForward;
		public bool YUp;
		/// <summary>When set, rigged human models are transferred onto this Medieval 2 skeleton (.glb).</summary>
		public string M2SkeletonPath;
		/// <summary>Write the m2 skeleton's animations into the fbx too.</summary>
		public bool M2IncludeAnimations = true;
		/// <summary>With <see cref="M2SkeletonPath"/>: rigged horse models are transferred onto this m2 horse (.glb).</summary>
		public string M2HorseSkeletonPath;
	}

	/// <summary>
	/// Exports gallery selections: models with all their textures, materials as their texture sets and
	/// plain textures. Safe to run on a background thread.
	/// </summary>
	public sealed class GalleryExporter
	{
		private static readonly string[] HorseKeywords =
			{ "horse", "camel", "mule", "donkey", "pony", "barding", "caparison", "saddle", "harness" };

		private readonly GalleryExportSettings _settings;
		private readonly HashSet<string> _usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private Skeleton _m2Skeleton;
		private List<global::Assimp.Animation> _m2Animations;
		/// <summary>The m2 horse clip written with horse exports.</summary>
		private const string HorseClip = "fs_horse_walk";
		private Skeleton _m2Horse;
		private List<global::Assimp.Animation> _m2HorseAnimations;

		public GalleryExporter(GalleryExportSettings settings)
		{
			_settings = settings;
		}

		/// <summary>Exports one asset and returns a short note for the log (may be null).</summary>
		public string Export(AssetItem asset)
		{
			switch (asset)
			{
				case Metamesh metamesh:
					return ExportModel(metamesh);
				case Material material:
					return ExportMaterial(material);
				case Texture texture:
					return ExportTexture(texture);
			}
			throw new NotSupportedException("Cannot export " + asset.GetType().Name);
		}

		/// <summary>Some meshes of one model, written together.</summary>
		private sealed class Part
		{
			public readonly Metamesh Model;
			public readonly List<Mesh> Meshes;
			/// <summary>What the LOD choice changed for this model, or null.</summary>
			public readonly string LodNote;

			public Part(Metamesh model, List<Mesh> meshes, string lodNote)
			{
				Model = model;
				Meshes = meshes;
				LodNote = lodNote;
			}
		}

		private string ExportModel(Metamesh model)
		{
			var notes = new List<string>();
			var results = PlanModels(new[] { model }, notes).Select(job => job.Run()).Where(n => n != null).ToList();
			notes.AddRange(results);
			return notes.Count > 0 ? string.Join(", ", notes) : null;
		}

		/// <summary>
		/// Spreads the models over files as the grouping and LOD settings say; one job per file. With separate
		/// LOD files every model / group gets one file per LOD (name_lod0, name_lod1, ...) in a shared folder.
		/// <paramref name="notes"/> gets what the plan left out, for the log.
		/// </summary>
		public List<GalleryExportJob> PlanModels(IReadOnlyList<Metamesh> models, List<string> notes)
		{
			var lods = _settings.Lods.Where(l => l >= 0).Distinct().OrderBy(l => l).ToList();
			if (lods.Count == 0)
				lods.Add(0);
			// each bucket is a set of files of its own, told apart by the suffix
			var buckets = _settings.SeparateLodFiles && lods.Count > 1
				? lods.Select(l => new KeyValuePair<string, List<int>>("_lod" + l, new List<int> { l })).ToList()
				: new List<KeyValuePair<string, List<int>>> { new KeyValuePair<string, List<int>>(string.Empty, lods) };

			var exported = new List<Metamesh>();
			foreach (var model in models)
			{
				if (model.Meshes.Count == 0)
					notes.Add(model.Name + ": skipped, no meshes");
				else
					exported.Add(model);
			}

			// a model / group keeps its folder name across the LOD buckets, so its LOD files sit together
			var modelFolders = new Dictionary<Metamesh, string>();
			var textureFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			string combinedFolder = null;
			var jobs = new List<GalleryExportJob>();

			foreach (var bucket in buckets)
			{
				var suffix = bucket.Key;
				var parts = new List<Part>();
				foreach (var model in exported)
				{
					var meshes = SelectMeshes(model, bucket.Value, lods, out var lodNote);
					if (meshes == null)
						notes.Add($"{model.Name}{suffix}: skipped, {lodNote}");
					else
						parts.Add(new Part(model, meshes, lodNote));
				}
				if (parts.Count == 0)
					continue;

				switch (_settings.Grouping)
				{
					case GalleryGrouping.PerModel:
						foreach (var part in parts)
						{
							if (!modelFolders.TryGetValue(part.Model, out var folder))
								modelFolders[part.Model] = folder = UniqueName(part.Model.Name);
							jobs.Add(Job(folder, folder + suffix, new List<Part> { part }));
						}
						break;

					case GalleryGrouping.Combined:
						if (combinedFolder == null)
							combinedFolder = UniqueName(string.IsNullOrWhiteSpace(_settings.CombinedName) ? "combined" : _settings.CombinedName);
						jobs.Add(Job(combinedFolder, combinedFolder + suffix, parts));
						break;

					case GalleryGrouping.ByTexture:
					case GalleryGrouping.SplitByTexture:
						var groups = new Dictionary<string, List<Part>>(StringComparer.OrdinalIgnoreCase);
						foreach (var part in parts)
						{
							if (_settings.Grouping == GalleryGrouping.ByTexture)
								AddToGroup(groups, TextureSetKey(part.Meshes), part);
							else
								foreach (var byTexture in part.Meshes.GroupBy(TextureKey, StringComparer.OrdinalIgnoreCase))
									AddToGroup(groups, byTexture.Key, new Part(part.Model, byTexture.ToList(), part.LodNote));
						}
						foreach (var group in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
						{
							if (!textureFolders.TryGetValue(group.Key, out var folder))
								textureFolders[group.Key] = folder = UniqueName(group.Key);
							jobs.Add(Job(folder, folder + suffix, group.Value));
						}
						break;
				}
			}
			return jobs;
		}

		private static void AddToGroup(Dictionary<string, List<Part>> groups, string key, Part part)
		{
			if (!groups.TryGetValue(key, out var list))
				groups[key] = list = new List<Part>();
			list.Add(part);
		}

		private GalleryExportJob Job(string folder, string fileName, List<Part> parts)
		{
			return new GalleryExportJob
			{
				Name = fileName + (parts.Count > 1 ? $" ({parts.Count} models)" : string.Empty),
				Run = () => ExportParts(folder, fileName, parts)
			};
		}

		/// <summary>
		/// Writes the parts into &lt;folder&gt;/&lt;fileName&gt;. With several models the mesh names get their
		/// model's name as a prefix so they stay unique and show which model they came from. The file is rigged
		/// only when every part uses the same skeleton. Repeated mesh parts are left out first when
		/// <see cref="GalleryExportSettings.RemoveDuplicateParts"/> applies. Returns a short note for the log.
		/// </summary>
		private string ExportParts(string folder, string fileName, List<Part> parts)
		{
			var note = new List<string>();
			if (!_settings.RemoveDuplicateParts || _settings.Grouping == GalleryGrouping.PerModel)
				return WriteParts(folder, fileName, parts, note);
			if (_settings.ModelExtension == "dae")
			{
				// the collada exporter writes the game's vertex streams, not the edit data trimmed here
				note.Add("duplicate parts kept (not supported for COLLADA)");
				return WriteParts(folder, fileName, parts, note);
			}

			// the trimmed mesh data must stay in place until the file is written
			lock (ModelExporter.SyncRoot)
			{
				using (var duplicates = DuplicatePartRemoval.Apply(parts.SelectMany(p => p.Meshes)))
				{
					if (duplicates.RemovedParts > 0)
					{
						var removedMeshes = parts.SelectMany(p => p.Meshes.Where(duplicates.RemovedMeshes.Contains)
							.Select(m => p.Model.Name + "/" + m.Name)).ToList();
						note.Add($"removed {duplicates.RemovedParts} duplicate part{(duplicates.RemovedParts == 1 ? "" : "s")}" +
						         (removedMeshes.Count > 0 ? $" (whole meshes: {string.Join(", ", removedMeshes)})" : ""));
						parts = parts.Select(p => new Part(p.Model,
								p.Meshes.Where(m => !duplicates.RemovedMeshes.Contains(m)).ToList(), p.LodNote))
							.Where(p => p.Meshes.Count > 0).ToList();
					}
					return WriteParts(folder, fileName, parts, note);
				}
			}
		}

		private string WriteParts(string folder, string fileName, List<Part> parts, List<string> note)
		{
			var multi = parts.Count > 1;
			// a single model keeps its name: the exporters recognise e.g. heads by it
			var view = MeshView(multi ? folder : parts[0].Model.Name, new List<Mesh>());
			var skeletons = new List<Skeleton>();
			var staticParts = new List<Part>();
			foreach (var part in parts)
			{
				var partNote = new List<string>();
				if (part.LodNote != null)
					partNote.Add(part.LodNote);
				view.Meshes.AddRange(part.Meshes);
				// in a shared file static models (weapons, shields, ...) don't decide the skeleton: they go along with it
				if (multi && IsStatic(part.Meshes))
					staticParts.Add(part);
				else
					skeletons.Add(PickSkeleton(part.Model.Name, part.Meshes, partNote));
				note.AddRange(multi ? partNote.Select(n => part.Model.Name + ": " + n) : partNote);
			}

			var skeleton = skeletons.FirstOrDefault();
			// parts written static in a rigged file, because they aren't rigged to its skeleton
			var demoted = new List<Part>();
			if (SplitsStatic)
			{
				// the file is rigged to the skeleton most rigged meshes use; the rest goes to its static part
				var riggedParts = parts.Where(p => !staticParts.Contains(p)).ToList();
				skeleton = riggedParts.Select((p, i) => new { Skeleton = skeletons[i], Meshes = p.Meshes.Count })
					.Where(s => s.Skeleton != null)
					.GroupBy(s => s.Skeleton)
					.OrderByDescending(g => g.Sum(s => s.Meshes))
					.Select(g => g.Key)
					.FirstOrDefault();
				if (skeleton != null)
				{
					for (int i = 0; i < riggedParts.Count; i++)
					{
						if (skeletons[i] == skeleton)
							continue;
						demoted.Add(riggedParts[i]);
						if (skeletons[i] != null)
							note.Add($"{riggedParts[i].Model.Name}: written static (rigged to {skeletons[i].Name}, " +
							         $"the file is rigged to {skeleton.Name})");
					}
				}
			}
			else if (skeletons.Any(s => s != skeleton))
			{
				// one file has one skeleton: rigged only when every rigged part is rigged to the same one
				skeleton = null;
				var used = skeletons.Select(s => s?.Name ?? "static").Distinct();
				note.Add("exported static: the rigged models don't share one skeleton (" + string.Join(", ", used) + ")");
			}
			if (!multi)
				return ExportView(folder, fileName, view, skeleton, note);
			note.Insert(0, $"{parts.Count} models: " + string.Join(", ", parts.Select(p => p.Model.Name)));

			// renamed for this export only; the lock keeps other exports from seeing the temporary names
			lock (ModelExporter.SyncRoot)
			{
				var originalNames = view.Meshes.Select(m => m.Name).ToList();
				var restores = new List<Action>();
				try
				{
					if (skeleton != null && demoted.Count > 0)
						Unbind(demoted, restores);
					if (skeleton != null && staticParts.Count > 0)
					{
						// split files keep static models static, except the ones held in a hand
						var bound = SplitsStatic
							? staticParts.Where(p => GetHold(p.Model.Name) != Hold.Root).ToList()
							: staticParts;
						if (bound.Count > 0)
							note.Add(BindStatic(bound, skeleton, restores));
						if (bound.Count < staticParts.Count)
							note.Add($"{staticParts.Count - bound.Count} static model{(staticParts.Count - bound.Count == 1 ? "" : "s")} " +
							         "in the static part");
					}
					var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (var part in parts)
					{
						var modelName = part.Model.Name;
						foreach (var mesh in part.Meshes)
						{
							var meshName = string.IsNullOrEmpty(mesh.Name) ? modelName
								: mesh.Name.StartsWith(modelName, StringComparison.OrdinalIgnoreCase) ? mesh.Name
								: modelName + "_" + mesh.Name;
							var unique = meshName;
							for (int i = 2; !names.Add(unique); i++)
								unique = meshName + "_" + i;
							mesh.Name = unique;
						}
					}
					return ExportView(folder, fileName, view, skeleton, note);
				}
				finally
				{
					for (int i = 0; i < originalNames.Count; i++)
						view.Meshes[i].Name = originalNames[i];
					foreach (var restore in restores)
						restore();
				}
			}
		}

		private static bool IsStatic(List<Mesh> meshes)
		{
			return meshes.All(m => m.SkinDataSize <= 0);
		}

		/// <summary>
		/// Whether a rigged file can hold static meshes too: the assimp fbx exporter writes meshes without
		/// weights into a static node beside the armature, so a rigged file has a rigged and a static part.
		/// </summary>
		private bool SplitsStatic => _settings.ModelExtension == "fbx" && AssimpModelExporter.IsAssimpAvailable();

#pragma warning disable 612
		/// <summary>Drops the weights of these parts for this export, so they are written static.</summary>
		private static void Unbind(List<Part> parts, List<Action> restores)
		{
			var visited = new HashSet<MeshEditData>();
			foreach (var mesh in parts.SelectMany(p => p.Meshes))
			{
				var data = mesh.EditData?.Data;
				if (data == null || !visited.Add(data))
					continue;
				SaveForRestore(data, restores);
				data.Bones = null;
			}
		}
#pragma warning restore 612

		private enum Hold { Root, LeftHand, RightHand }

		private static readonly string[] LeftHandWords = { "shield", "buckler" };

		private static readonly string[] RightHandWords =
		{
			"sword", "blade", "guard", "handle", "pommel", "lance", "spear", "pike", "javelin", "axe", "mace",
			"hammer", "club", "dagger", "knife", "glaive", "halberd", "polearm", "staff", "crossbow", "weapon"
		};

		/// <summary>Which hand holds a static model, from the words of its name.</summary>
		private static Hold GetHold(string modelName)
		{
			var words = modelName.ToLowerInvariant().Split('_', ' ', '.', '-');
			if (words.Any(w => LeftHandWords.Any(k => w.Contains(k))))
				return Hold.LeftHand;
			if (words.Any(w => w.Contains("crossbow")))
				return Hold.RightHand;
			if (words.Any(w => w.EndsWith("bow"))) // bows are held in the left hand
				return Hold.LeftHand;
			if (words.Any(w => RightHandWords.Any(k => w.Contains(k))))
				return Hold.RightHand;
			return Hold.Root;
		}

#pragma warning disable 612
		/// <summary>
		/// Binds static models to <paramref name="skeleton"/> so they can go into a rigged file (the exporters
		/// write one weight set per mesh): shields and bows to l_hand, other weapons to r_hand, anything else to
		/// the root bone. A model held in a hand is moved by the hand's rest frame, so its origin (the grip in
		/// bannerlord) sits on the hand joint, and it follows the hand through the T-pose and the Medieval 2
		/// transfer. <paramref name="restores"/> undoes the changes and keeps the weakly loaded data alive until
		/// then. Returns a note for the log.
		/// </summary>
		private static string BindStatic(List<Part> parts, Skeleton skeleton,
			List<Action> restores)
		{
			var definition = skeleton.Definition.Data;
			var bones = definition.Bones;
			// the same bone space the exporters and the T-pose use
			var globals = definition.CreateBoneMatrices(TPoseConverter.IsIgnoreScaleSkeleton(skeleton));
			int Find(string boneName) =>
				bones.FindIndex(b => string.Equals(b.Name, boneName, StringComparison.OrdinalIgnoreCase));
			var leftHand = Find("l_hand");
			var rightHand = Find("r_hand");

			var counts = new Dictionary<string, int>();
			var visited = new HashSet<MeshEditData>();
			foreach (var part in parts)
			{
				var hold = GetHold(part.Model.Name);
				var bone = hold == Hold.LeftHand ? leftHand : hold == Hold.RightHand ? rightHand : -1;
				var inHand = bone >= 0;
				if (!inHand)
					bone = 0;
				var boneName = bones[bone].Name;
				counts[boneName] = (counts.TryGetValue(boneName, out var count) ? count : 0) + 1;
				var frame = RigidFrame(globals[bone]);

				foreach (var mesh in part.Meshes)
				{
					var data = mesh.EditData?.Data;
					if (data == null || !visited.Add(data))
						continue;
					SaveForRestore(data, restores);
					if (inHand)
						Transform(data, frame);
					var weights = new MeshEditData.Bone[data.Positions.Length];
					for (int i = 0; i < weights.Length; i++)
					{
						weights[i].W0 = 1f;
						weights[i].B0 = (byte) bone;
					}
					data.Bones = weights;
				}
			}
			return "static models bound to " + string.Join(", ", counts.Select(p => $"{p.Key} ({p.Value})"));
		}

		private static void SaveForRestore(MeshEditData data, List<Action> restores)
		{
			var positions = data.Positions;
			var vertices = data.Vertices;
			var weights = data.Bones;
			var frames = data.MorphFrames.Select(f => Tuple.Create(f, f.Positions, f.Normals)).ToList();
			restores.Add(() =>
			{
				data.Positions = positions;
				data.Vertices = vertices;
				data.Bones = weights;
				foreach (var frame in frames)
				{
					frame.Item1.Positions = frame.Item2;
					frame.Item1.Normals = frame.Item3;
				}
			});
		}
#pragma warning restore 612

		/// <summary>The rotation and translation of a bone frame, without its scale.</summary>
		private static Matrix4x4 RigidFrame(Matrix4x4 frame)
		{
			if (!Matrix4x4.Decompose(frame, out _, out var rotation, out var translation))
				return frame;
			return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(translation);
		}

		/// <summary>Moves a mesh (positions, normals, tangents and morph targets) by a rigid frame.</summary>
		private static void Transform(MeshEditData data, Matrix4x4 frame)
		{
			Vector4 Point(Vector4 p) => new Vector4(Vector3.Transform(new Vector3(p.X, p.Y, p.Z), frame), p.W);
			Vector4 Direction(Vector4 d) => new Vector4(Vector3.TransformNormal(new Vector3(d.X, d.Y, d.Z), frame), d.W);

			data.Positions = data.Positions.Select(Point).ToArray();
			var vertices = (MeshEditData.Vertex[]) data.Vertices.Clone();
			for (int i = 0; i < vertices.Length; i++)
			{
				vertices[i].Normal = Direction(vertices[i].Normal);
				vertices[i].Tangent = Direction(vertices[i].Tangent);
				vertices[i].Binormal = Direction(vertices[i].Binormal);
				vertices[i].Padding = Direction(vertices[i].Padding);
			}
			data.Vertices = vertices;
			foreach (var morph in data.MorphFrames)
			{
				morph.Positions = morph.Positions?.Select(Point).ToArray();
				morph.Normals = morph.Normals?.Select(Direction).ToArray();
			}
		}

		/// <summary>
		/// The meshes of the <paramref name="requested"/> LODs. A model without one of them uses its nearest lower
		/// LOD, or its most detailed one when it only has coarser LODs; <paramref name="note"/> says so. Returns
		/// null when the LOD it would fall back to is already written for another entry of
		/// <paramref name="allRequested"/> (separate LOD files only).
		/// </summary>
		private static List<Mesh> SelectMeshes(Metamesh model, List<int> requested, List<int> allRequested, out string note)
		{
			note = null;
			var available = model.Meshes.Select(m => m.Lod).Distinct().ToList();
			int Resolve(int lod)
			{
				var lower = available.Where(l => l <= lod).ToList();
				return lower.Count > 0 ? lower.Max() : available.Min();
			}

			var chosen = new HashSet<int>();
			var substitutes = new List<string>();
			foreach (var lod in requested)
			{
				var used = Resolve(lod);
				if (used != lod)
				{
					if (requested.Count == 1 && (allRequested.Contains(used) ||
					                             allRequested.Any(other => other < lod && Resolve(other) == used)))
					{
						note = $"has no LOD {lod} (its LOD {used} is already exported)";
						return null;
					}
					substitutes.Add(requested.Contains(used) ? $"has no LOD {lod}" : $"LOD {used} (has no LOD {lod})");
				}
				chosen.Add(used);
			}
			if (substitutes.Count > 0)
				note = string.Join(", ", substitutes);
			return model.Meshes.FindAll(m => chosen.Contains(m.Lod));
		}

		/// <summary>
		/// The texture a mesh is grouped by: its material's diffuse texture (found by name), else the material's
		/// first texture, else the material's name.
		/// </summary>
		private static string TextureKey(Mesh mesh)
		{
			if (!mesh.DrawnMaterial.TryGetItem(out var material))
				return "no_material";
			Texture first = null;
			foreach (var dependence in material.Textures.Values)
			{
				if (!dependence.TryGetItem(out var texture))
					continue;
				if (IsDiffuse(texture.Name))
					return texture.Name;
				if (first == null)
					first = texture;
			}
			return first?.Name ?? material.Name;
		}

		/// <summary>The textures of a whole model: "tex1+tex2"; past three only the first two are named.</summary>
		private static string TextureSetKey(List<Mesh> meshes)
		{
			var keys = meshes.Select(TextureKey).Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
			return keys.Count <= 3
				? string.Join("+", keys)
				: string.Join("+", keys.Take(2)) + $"+{keys.Count - 2}_more";
		}

		private static bool IsDiffuse(string textureName)
		{
			var name = textureName.ToLowerInvariant();
			return name.EndsWith("_d") || name.EndsWith("_diffuse") || name.EndsWith("_d_4k") ||
			       name.EndsWith("_c") || name.EndsWith("_color") || name.EndsWith("_base_color") ||
			       name.EndsWith("_c_4k");
		}

		/// <summary>A metamesh holding just the given meshes, so the exporters write exactly those.</summary>
		private static Metamesh MeshView(string name, List<Mesh> meshes)
		{
			var view = new Metamesh { Name = name };
			view.Meshes.AddRange(meshes);
			return view;
		}

		/// <summary>
		/// Writes every mesh of <paramref name="view"/> to &lt;folder&gt;/&lt;fileName&gt;.&lt;ext&gt; (without the
		/// folder when "one folder per asset" is off).
		/// </summary>
		private string ExportView(string folder, string fileName, Metamesh view, Skeleton skeleton, List<string> note)
		{
			var meshes = view.Meshes;
			var dir = _settings.OneFolderPerAsset ? Path.Combine(_settings.OutputDir, folder) : _settings.OutputDir;
			Directory.CreateDirectory(dir);
			var path = Path.Combine(dir, fileName + "." + _settings.ModelExtension);

			// the view only holds the meshes to export, so every LOD in it is written
			var option = ModelExporter.ModelExportOption.ExportAllLod;
			// one folder per asset: textures sit next to the model. otherwise: <model name>/ sub folder
			option |= _settings.OneFolderPerAsset
				? ModelExporter.ModelExportOption.ExportTextures
				: ModelExporter.ModelExportOption.ExportTexturesSubFolder;
			if (_settings.TexturePreference == 1)
				option |= ModelExporter.ModelExportOption.PreferPng;
			else if (_settings.TexturePreference == 2)
				option |= ModelExporter.ModelExportOption.PreferDds;
			if (_settings.DiffuseOnly)
				option |= ModelExporter.ModelExportOption.ExportDiffuseOnly;
			if (_settings.LargerScale)
				option |= ModelExporter.ModelExportOption.LargerSize;
			if (_settings.NegYForward)
				option |= ModelExporter.ModelExportOption.NegYAxisForward;
			if (_settings.YUp)
				option |= ModelExporter.ModelExportOption.YAxisUp;
			if (_settings.ConvertToTPose && skeleton != null)
			{
				option |= ModelExporter.ModelExportOption.ConvertToTPose;
				if (!_settings.StraightenElbows)
					option |= ModelExporter.ModelExportOption.KeepElbowBend;
			}

			if (skeleton != null && !string.IsNullOrEmpty(_settings.M2SkeletonPath))
			{
				if (IsBannerlordHuman(skeleton))
				{
					ExportToMedieval2(path, view, meshes, skeleton, option, note);
					return string.Join(", ", note);
				}
				if (M2RigTransfer.IsBannerlordHorse(skeleton) && !string.IsNullOrEmpty(_settings.M2HorseSkeletonPath))
				{
					ExportHorseToMedieval2(path, view, meshes, skeleton, option, note);
					return string.Join(", ", note);
				}
				note.Add("Medieval 2 transfer skipped: only the human and horse skeletons can be transferred");
			}

			ExportFile(path, view, skeleton, option);

			if (skeleton != null)
				note.Add("rigged to " + skeleton.Name);
			var tPose = ModelExporter.LastTPoseResult;
			if (tPose != null)
				note.Add(tPose.HasChanges ? "T-pose" : "T-pose skipped (" + string.Join("; ", tPose.Log) + ")");
			return note.Count > 0 ? string.Join(", ", note) : null;
		}

		private void ExportFile(string path, Metamesh model, Skeleton skeleton, ModelExporter.ModelExportOption option)
		{
			if (_settings.ModelExtension == "fbx" && AssimpModelExporter.IsAssimpAvailable())
				AssimpModelExporter.ExportToFile(path, model, skeleton, option);
			else
				ModelExporter.ExportToFile(path, model, skeleton, option);
		}

		private static bool IsBannerlordHuman(Skeleton skeleton)
		{
			var names = new HashSet<string>(skeleton.Definition.Data.Bones.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
			return names.Contains("pelvis") && names.Contains("spine2") && names.Contains("l_clavicle") &&
			       names.Contains("r_clavicle");
		}

		/// <summary>
		/// T-poses the model on its bannerlord skeleton (m2 skeletons are in T-pose), moves it and its weights
		/// onto the m2 skeleton and exports it with the m2 skeleton.
		/// </summary>
		private void ExportToMedieval2(string path, Metamesh model, List<Mesh> meshes, Skeleton skeleton,
			ModelExporter.ModelExportOption option, List<string> note)
		{
			var withAnimations = _settings.M2IncludeAnimations && _settings.ModelExtension == "fbx" &&
			                     AssimpModelExporter.IsAssimpAvailable();
			if (_m2Skeleton == null)
				_m2Skeleton = M2RigTransfer.LoadSkeleton(_settings.M2SkeletonPath, withAnimations, out _m2Animations);

			// the m2 space is fixed (see M2RigTransfer): no axis options, and the T-pose is done here
			option &= ~(ModelExporter.ModelExportOption.ConvertToTPose |
			            ModelExporter.ModelExportOption.KeepElbowBend |
			            ModelExporter.ModelExportOption.NegYAxisForward |
			            ModelExporter.ModelExportOption.YAxisUp |
			            ModelExporter.ModelExportOption.LargerSize);

			List<string> log;
			lock (ModelExporter.SyncRoot)
			{
				using (var tPose = TPoseConverter.Apply(skeleton, meshes, _settings.StraightenElbows))
				{
					if (!tPose.Converter.HasChanges)
						note.Add("T-pose skipped (" + string.Join("; ", tPose.Converter.Log) + ")");
					using (M2RigTransfer.Apply(skeleton, _m2Skeleton, meshes, out log))
					{
						if (withAnimations)
							AssimpModelExporter.ExportToFile(path, model, _m2Skeleton, _m2Animations, option);
						else
							ExportFile(path, model, _m2Skeleton, option);
					}
				}
			}
			note.Add($"transferred from {skeleton.Name} to Medieval 2 {_m2Skeleton.Name}" +
			         (withAnimations ? $" with {_m2Animations.Count} animations" : ""));
			note.AddRange(log);
		}

		/// <summary>
		/// Moves a bannerlord horse model and its weights onto the vanilla m2 horse and exports it with that
		/// skeleton (horses have no A-pose to undo).
		/// </summary>
		private void ExportHorseToMedieval2(string path, Metamesh model, List<Mesh> meshes, Skeleton skeleton,
			ModelExporter.ModelExportOption option, List<string> note)
		{
			var withAnimations = _settings.M2IncludeAnimations && _settings.ModelExtension == "fbx" &&
			                     AssimpModelExporter.IsAssimpAvailable();
			if (_m2Horse == null)
			{
				_m2Horse = M2RigTransfer.LoadSkeleton(_settings.M2HorseSkeletonPath, withAnimations, out var clips);
				// horses get the walk: the clip to check a horse against in blender
				var walk = clips.Where(c => c.Name.EndsWith(HorseClip, StringComparison.OrdinalIgnoreCase)).ToList();
				_m2HorseAnimations = walk.Count > 0 ? walk : clips;
			}

			option &= ~(ModelExporter.ModelExportOption.ConvertToTPose |
			            ModelExporter.ModelExportOption.KeepElbowBend |
			            ModelExporter.ModelExportOption.NegYAxisForward |
			            ModelExporter.ModelExportOption.YAxisUp |
			            ModelExporter.ModelExportOption.LargerSize);

			List<string> log;
			lock (ModelExporter.SyncRoot)
			{
				using (M2RigTransfer.ApplyHorse(skeleton, _m2Horse, meshes, out log))
				{
					if (withAnimations)
						AssimpModelExporter.ExportToFile(path, model, _m2Horse, _m2HorseAnimations, option);
					else
						ExportFile(path, model, _m2Horse, option);
				}
			}
			note.Add($"transferred from {skeleton.Name} to the Medieval 2 horse {_m2Horse.Name}" +
			         (withAnimations ? " with " + string.Join(", ", _m2HorseAnimations.Select(a => a.Name)) : ""));
			note.AddRange(log);
		}

		private Skeleton PickSkeleton(string modelName, List<Mesh> meshes, List<string> note)
		{
			if (_settings.SkeletonMode == GallerySkeletonMode.None)
				return null;

			if (meshes.Count == 0 || meshes.All(m => m.SkinDataSize <= 0))
				return null; // static model

			if (SplitsStatic)
			{
				// the skinned meshes decide the skeleton, the others go to the file's static part
				var skinned = meshes.Where(m => m.SkinDataSize > 0 && HasWeights(m)).ToList();
				if (skinned.Count == 0)
				{
					note.Add("exported static: no mesh has weights");
					return null;
				}
				if (skinned.Count < meshes.Count)
					note.Add($"{meshes.Count - skinned.Count} unskinned mesh{(meshes.Count - skinned.Count == 1 ? "" : "es")} " +
					         "in the static part");
				meshes = skinned;
			}
			// the other exporters write one weight set per mesh: every exported mesh must be skinned
			else if (meshes.Any(m => m.SkinDataSize <= 0 || !HasWeights(m)))
			{
				note.Add("exported static: not every mesh is skinned");
				return null;
			}

			Skeleton skeleton;
			if (_settings.SkeletonMode == GallerySkeletonMode.Specific)
			{
				skeleton = _settings.Skeleton;
			}
			else
			{
				var lower = modelName.ToLowerInvariant();
				var isHorse = HorseKeywords.Any(k => lower.Contains(k));
				if (isHorse && _settings.HorseSkeleton == null && meshes.Any(m => MaxBoneIndex(m) >=
					    (_settings.HumanSkeleton?.Definition?.Data?.Bones.Count ?? 0)))
				{
					// horse gear rigged past the human bones: only bannerlord's horse_skeleton (Native module) fits
					note.Add("exported static: horse_skeleton not loaded (load the Native module's AssetPackages too)");
					return null;
				}
				skeleton = isHorse ? _settings.HorseSkeleton ?? _settings.HumanSkeleton : _settings.HumanSkeleton;
			}

			if (skeleton == null)
			{
				note.Add("exported static: skeleton not loaded");
				return null;
			}

			// bone indices must exist in the chosen skeleton or the exporter would index out of range
			var boneCount = skeleton.Definition?.Data?.Bones.Count ?? 0;
			if (boneCount == 0 || meshes.Any(m => MaxBoneIndex(m) >= boneCount))
			{
				note.Add($"exported static: weights don't fit {skeleton.Name}");
				return null;
			}
			return skeleton;
		}

#pragma warning disable 612
		private static bool HasWeights(Mesh mesh)
		{
			var data = mesh.EditData?.Data;
			return data?.Bones != null && data.Positions != null && data.Bones.Length >= data.Positions.Length;
		}

		private static int MaxBoneIndex(Mesh mesh)
		{
			var bones = mesh.EditData?.Data?.Bones;
			if (bones == null)
				return -1;
			int max = -1;
			foreach (var b in bones)
			{
				if (b.W0 > 0) max = Math.Max(max, b.B0);
				if (b.W1 > 0) max = Math.Max(max, b.B1);
				if (b.W2 > 0) max = Math.Max(max, b.B2);
				if (b.W3 > 0) max = Math.Max(max, b.B3);
			}
			return max;
		}
#pragma warning restore 612

		private string ExportMaterial(Material material)
		{
			var name = UniqueName(material.Name);
			var dir = Path.Combine(_settings.OutputDir, _settings.OneFolderPerAsset ? name : "materials\\" + name);
			int count = 0, missing = 0;
			foreach (var dependence in material.Textures.Values)
			{
				if (!dependence.TryGetItem(out var texture) || !texture.HasPixelData)
				{
					missing++;
					continue;
				}
				Directory.CreateDirectory(dir);
				TextureExporter.ExportToFile(Path.Combine(dir, SafeName(texture.Name) + "." + TextureExtension(texture)), texture);
				count++;
			}
			if (count == 0)
				throw new InvalidOperationException("material has no exportable textures");
			return $"{count} textures" + (missing > 0 ? $", {missing} missing" : "");
		}

		private string ExportTexture(Texture texture)
		{
			if (!texture.HasPixelData)
				throw new InvalidOperationException("texture has no pixel data");
			var dir = Path.Combine(_settings.OutputDir, "textures");
			Directory.CreateDirectory(dir);
			var name = UniqueName(texture.Name);
			TextureExporter.ExportToFile(Path.Combine(dir, name + "." + TextureExtension(texture)), texture);
			return null;
		}

		private string TextureExtension(Texture texture)
		{
			MaterialExporter.MaterialExportOption option = 0;
			if (_settings.TexturePreference == 1)
				option = MaterialExporter.MaterialExportOption.PreferPng;
			else if (_settings.TexturePreference == 2)
				option = MaterialExporter.MaterialExportOption.PreferDds;
			return MaterialExporter.GetBestTextureFormat(texture.Format, option);
		}

		private string UniqueName(string name)
		{
			var safe = SafeName(name);
			var result = safe;
			for (int i = 2; !_usedNames.Add(result); i++)
				result = safe + "_" + i;
			return result;
		}

		private static string SafeName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return "unnamed";
			var invalid = Path.GetInvalidFileNameChars();
			return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
		}
	}
}
