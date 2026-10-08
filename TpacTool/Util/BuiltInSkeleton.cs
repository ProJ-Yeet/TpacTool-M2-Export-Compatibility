using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TpacTool.Lib;

namespace TpacTool
{
	/// <summary>
	/// A copy of bannerlord's 28-bone human skeleton (pelvis ... r_finger0, in the game's bone order), used
	/// for rigged exports when the loaded asset folder doesn't contain a human skeleton itself (e.g. a
	/// mod's module folder: the skeleton ships with the Native module).
	/// </summary>
	public static class BuiltInSkeleton
	{
		public const string HumanName = "human_skeleton (built-in)";

		private static Skeleton _human;

		public static Skeleton Human
		{
			get
			{
				if (_human == null)
					_human = CreateHuman();
				return _human;
			}
		}

		/// <summary>
		/// Picks the human skeleton from the loaded assets: "human_skeleton" itself, otherwise a variant
		/// such as "human_skeleton_notused.004" that mods ship, otherwise null.
		/// </summary>
		public static Skeleton FindLoadedHuman(IEnumerable<Skeleton> skeletons)
		{
			return FindByPrefix(skeletons, "human_skeleton");
		}

		public static Skeleton FindLoadedHorse(IEnumerable<Skeleton> skeletons)
		{
			return FindByPrefix(skeletons, "horse_skeleton");
		}

		private static Skeleton FindByPrefix(IEnumerable<Skeleton> skeletons, string prefix)
		{
			var candidates = skeletons.Where(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
			                                      s.Definition?.Data != null).ToList();
			return candidates.FirstOrDefault(s => s.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase)) ??
			       candidates.OrderBy(s => s.Name.Length).ThenBy(s => s.Name).FirstOrDefault();
		}

		private static Skeleton CreateHuman()
		{
			var data = new SkeletonDefinitionData { Name = "human_skeleton" };
			foreach (var (name, parent, frame) in HumanBones)
			{
				data.Bones.Add(new BoneNode
				{
					Name = name,
					Parent = parent >= 0 ? data.Bones[parent] : null,
					RestFrame = frame
				});
			}
			return new Skeleton
			{
				Name = "human_skeleton",
				Definition = new ExternalLoader<SkeletonDefinitionData>(data)
			};
		}

		private static (string, int, Matrix4x4) Bone(string name, int parent,
			float m11, float m12, float m13, float m14, float m21, float m22, float m23, float m24,
			float m31, float m32, float m33, float m34, float m41, float m42, float m43, float m44)
		{
			return (name, parent, new Matrix4x4(m11, m12, m13, m14, m21, m22, m23, m24,
				m31, m32, m33, m34, m41, m42, m43, m44));
		}

		private static readonly (string, int, Matrix4x4)[] HumanBones =
		{
			Bone("pelvis", -1, -0.00132691232f, -0.9999851f, -0.005294164f, 0f, 4.04995762E-06f, -0.00529417442f, 0.999986f, 0f, -0.9999991f, 0.00132687227f, 1.10748024E-05f, 0f, -1.984006E-07f, 0.0201696157f, 0.914520264f, 1f),
			Bone("l_thigh", 0, 0.9975559f, 0.06943425f, -0.007813067f, 0f, 0.0698361844f, -0.994388044f, 0.07947031f, 0f, -0.00225125952f, -0.07982171f, -0.9968067f, 0f, 0.000170062034f, 0.0005291958f, 0.100083575f, 1f),
			Bone("l_calf", 1, 0.9984848f, -0.05500831f, -0.00149413862f, 0f, 0.0550102219f, 0.998484969f, 0.00127102574f, 0f, 0.001421958f, -0.00135129283f, 0.9999981f, 0f, -7.392372E-09f, 0.418810368f, 3.282639E-08f, 1f),
			Bone("l_foot", 2, 0.531710446f, 0.8420124f, 0.0911001042f, 0f, -0.843235f, 0.53635323f, -0.03577615f, 0f, -0.0789857954f, -0.0577962473f, 0.995198965f, 0f, -1.37370062E-08f, 0.409857184f, -3.81171468E-08f, 1f),
			Bone("l_toe0", 3, 0.9934943f, 3.89890459E-07f, -0.113882847f, 0f, -3.803137E-07f, 1f, 1.05818735E-07f, 0f, 0.113882847f, -6.18190938E-08f, 0.9934943f, 0f, -2.32830616E-10f, 0.150985673f, 5.33836939E-08f, 1f),
			Bone("r_thigh", 0, 0.9975326f, 0.0701658f, 0.00237952173f, 0f, 0.07014038f, -0.99455905f, -0.0770248f, 0f, -0.00303793186f, 0.07700165f, -0.9970265f, 0f, -0.000168147642f, -0.000530064746f, -0.100084551f, 1f),
			Bone("r_calf", 5, 0.9984853f, -0.0550003462f, -0.00146968057f, 0f, 0.0550021455f, 0.998485565f, 0.00121416757f, 0f, 0.00140067516f, -0.0012931641f, 0.9999983f, 0f, -1.22818147E-08f, 0.418809921f, -1.84409128E-08f, 1f),
			Bone("r_foot", 6, 0.5265459f, 0.844954848f, -0.09381289f, 0f, -0.84569025f, 0.5318709f, 0.04383364f, 0f, 0.08693379f, 0.05625622f, 0.9946245f, 0f, 3.25962857E-09f, 0.4098564f, 2.36581137E-08f, 1f),
			Bone("r_toe0", 7, 0.9934945f, 1.314676E-07f, 0.113880672f, 0f, -1.42108163E-07f, 1f, 8.53180637E-08f, 0f, -0.113880672f, -1.009464E-07f, 0.9934945f, 0f, -7.45057971E-09f, 0.150305346f, -8.963979E-09f, 1f),
			Bone("spine", 0, 0.999752462f, -0.02221256f, -0.0013268796f, 0f, 0.0222125445f, 0.9997533f, -2.52382742E-05f, 0f, 0.0013271129f, -4.24134669E-06f, 0.9999992f, 0f, -3.320085E-09f, 0.0943935961f, -1.399769E-12f, 1f),
			Bone("spine1", 9, 0.9999999f, 0.0005213051f, 3.974097E-10f, 0f, -0.000521305134f, 0.99999994f, -1.03495024E-09f, 0f, -3.97949174E-10f, 1.034743E-09f, 1.00000012f, 0f, 1.8626467E-09f, 0.156860575f, 7.48290251E-14f, 1f),
			Bone("spine2", 10, 1f, 0.0001839194f, -3.01935552E-06f, 0f, -0.000183919386f, 1f, 2.93937524E-06f, 0f, 3.01989621E-06f, -2.93882E-06f, 1f, 0f, -3.497202E-15f, 0.139449209f, 5.50670552E-14f, 1f),
			Bone("neck", 11, 0.985598f, 0.169105008f, 3.1645875E-06f, 0f, -0.169105008f, 0.985598f, -2.793212E-06f, 0f, -3.5913572E-06f, 2.21783648E-06f, 1f, 0f, -2.79565726E-09f, 0.161071077f, 1.72306586E-13f, 1f),
			Bone("head", 12, 1f, -1.266597E-07f, 6.528172E-09f, 0f, 1.266597E-07f, 1f, -1.07440306E-08f, 0f, -6.52817045E-09f, 1.07440314E-08f, 1f, 0f, -3.166462E-08f, 0.108339347f, 2.584599E-13f, 1f),
			Bone("l_clavicle", 11, 0.999640644f, 0.0268080682f, 9.89956152E-06f, 0f, -9.738239E-06f, -6.14823966E-06f, 1f, 0f, 0.0268080682f, -0.999640644f, -5.884967E-06f, 0f, 0.02239211f, 0.109134518f, 0.0471458249f, 1f),
			Bone("l_upperarm_twist", 14, 0.9480984f, 0.146056369f, -0.282448322f, 0f, 0.03552034f, 0.8340591f, 0.550530553f, 0f, 0.31598708f, -0.5319898f, 0.7855821f, 0f, 5.70957948E-09f, 0.1391251f, 1.02670704E-07f, 1f),
			Bone("l_upperarm_twist1", 15, 0.9605484f, 3.568934E-06f, -0.278113216f, 0f, -2.99688668E-06f, 1f, 2.48200831E-06f, 0f, 0.278113216f, -1.55061537E-06f, 0.9605484f, 0f, 1.825392E-07f, 0.144439235f, 5.78351262E-07f, 1f),
			Bone("l_foretwist", 16, -0.966432f, -0.0864994451f, -0.241923943f, 0f, -0.07624604f, 0.995761f, -0.0514466129f, 0f, 0.245348513f, -0.03127391f, -0.9689305f, 0f, -9.12696E-08f, 0.144439042f, 2.38418551E-07f, 1f),
			Bone("l_foretwist1", 17, 0.999959767f, 3.563869E-06f, -0.008969375f, 0f, -3.63339586E-06f, 1f, -7.735268E-06f, 0f, 0.008969375f, 7.767546E-06f, 0.999959767f, 0f, -8.475035E-08f, 0.133822873f, 1.34110437E-07f, 1f),
			Bone("l_hand", 18, -0.997480154f, 0.0592639f, -0.0390036479f, 0f, 0.07049903f, 0.889647245f, -0.4511737f, 0f, 0.007961176f, -0.4527865f, -0.8915835f, 0f, 3.073364E-08f, 0.133822992f, 3.702007E-08f, 1f),
			Bone("l_finger0", 19, -0.771326542f, 3.502262E-07f, 0.636439741f, 0f, 1.89940735E-07f, 1f, -3.20092937E-07f, 0f, -0.636439741f, -1.26010349E-07f, -0.771326542f, 0f, 1.1362134E-07f, 0.07997969f, -2.00234336E-08f, 1f),
			Bone("r_clavicle", 11, 0.9996408f, 0.02679954f, 8.612201E-06f, 0f, 8.715706E-06f, -3.74538627E-06f, -1f, 0f, -0.02679954f, 0.9996408f, -3.977618E-06f, 0f, 0.0223924033f, 0.109133743f, -0.0471462458f, 1f),
			Bone("r_upperarm_twist", 21, 0.9480418f, 0.1461781f, 0.282575279f, 0f, 0.0355457477f, 0.8339647f, -0.550671756f, 0f, -0.316153944f, 0.5321042f, 0.785437346f, 0f, -4.24571978E-09f, 0.139126033f, -1.51741819E-09f, 1f),
			Bone("r_upperarm_twist1", 22, 0.9605925f, -5.14834937E-05f, 0.2779606f, 0f, 1.005451E-05f, 1f, 0.000150471707f, 0f, -0.2779606f, -0.000141747238f, 0.9605925f, 0f, -6.705522E-08f, 0.144438833f, 2.10478873E-07f, 1f),
			Bone("r_foretwist", 23, -0.9664523f, -0.08647871f, 0.241850168f, 0f, -0.0762246251f, 0.99576205f, 0.0514564663f, 0f, -0.24527511f, 0.03129528f, -0.9689483f, 0f, 1.7136334E-07f, 0.1444397f, 2.14204174E-08f, 1f),
			Bone("r_foretwist1", 24, 0.999961853f, 5.49816177E-05f, 0.00873496f, 0f, -5.35993931E-05f, 1f, -0.000158474271f, 0f, -0.00873496849f, 0.000158000039f, 0.999961853f, 0f, -1.86264493E-09f, 0.133823216f, -6.705522E-08f, 1f),
			Bone("r_hand", 25, -0.991531134f, -0.0180716813f, 0.128605738f, 0f, 0.0297144838f, 0.9324328f, 0.36011973f, 0f, -0.126424178f, 0.360891372f, -0.923999131f, 0f, 8.102506E-08f, 0.1338227f, 1.37137235E-07f, 1f),
			Bone("r_finger0", 26, -0.699999f, -6.499652E-07f, -0.7141439f, 0f, -5.411484E-08f, 1f, -8.57089E-07f, 0f, 0.7141439f, -5.6131563E-07f, -0.699999f, 0f, 4.00468672E-08f, 0.08564345f, -1.06636421E-07f, 1f)
		};
	}
}
