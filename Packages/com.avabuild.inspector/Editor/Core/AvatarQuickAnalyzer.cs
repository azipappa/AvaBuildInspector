using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace AvaBuildInspector
{
    internal static class AvatarQuickAnalyzer
    {
        public static AvatarReport Analyze(GameObject avatarRoot, TargetPlatform platform)
        {
            if (avatarRoot == null) throw new ArgumentNullException(nameof(avatarRoot));

            var mobile = platform == TargetPlatform.Android;
            var stats = new AvatarPerformanceStats(mobile);
            AvatarPerformance.CalculatePerformanceStats(avatarRoot.name, avatarRoot, stats, mobile);

            var report = new AvatarReport
            {
                AvatarRoot = avatarRoot,
                Platform = platform,
                Timestamp = DateTime.Now,
                OverallRank = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall)
            };

            AddSizeMetrics(report, stats);
            AddMeshMetrics(report, stats);
            AddDynamicsMetrics(report, stats);
            AddRenderingMetrics(report, stats);
            TryContributors(report, "Texture", () => AddTextureContributors(report, avatarRoot));
            TryContributors(report, "Mesh / Material", () => AddMeshContributors(report, avatarRoot));
            TryContributors(report, "PhysBone", () => AddPhysBoneContributors(report, avatarRoot));
            TryContributors(report, "Bounds", () => AddBoundsContributors(report, avatarRoot));
            TryContributors(report, "Contacts", () => AddContactContributors(report, avatarRoot));
            TryContributors(report, "Constraints", () => AddConstraintContributors(report, avatarRoot));
            TryContributors(report, "Animation", () => AddAnimationContributors(report, avatarRoot));
            TryContributors(report, "Rendering", () => AddRenderingContributors(report, avatarRoot));
            TryContributors(report, "Particles", () => AddParticleContributors(report, avatarRoot));
            TryContributors(report, "Cloth", () => AddClothContributors(report, avatarRoot));
            TryContributors(report, "Physics", () => AddPhysicsContributors(report, avatarRoot));
            TryContributors(report, "Audio", () => AddAudioContributors(report, avatarRoot));
            return report;
        }

        public static void ApplyExactDownloadSize(AvatarReport report, long byteCount)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            var metric = report.Find(AvatarPerformanceCategory.DownloadSize);
            if (metric == null) return;

            var safeBytes = (int)Math.Min(int.MaxValue, Math.Max(0L, byteCount));
            var evaluator = new AvatarPerformanceStats(report.Platform == TargetPlatform.Android)
            {
                downloadSizeBytes = safeBytes
            };
            metric.NumericValue = byteCount / 1024d / 1024d;
            metric.DisplayValue = metric.NumericValue.Value.ToString("0.##", CultureInfo.InvariantCulture) + " MB";
            metric.Accuracy = AnalysisAccuracy.Exact;
            metric.Explanation = "VRChat SDKが生成したAssetBundleのファイルサイズです。";
            metric.Rank = evaluator.GetPerformanceRatingForCategory(AvatarPerformanceCategory.DownloadSize);
            if ((int)metric.Rank > (int)report.OverallRank) report.OverallRank = metric.Rank;
        }

        private static void TryContributors(AvatarReport report, string analyzerName, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                report.Warnings.Add(analyzerName + " analysis failed: " + exception.Message);
                Debug.LogWarning("[AvaBuild Inspector] " + analyzerName + " analysis failed: " + exception);
            }
        }

        private static void AddSizeMetrics(AvatarReport report, AvatarPerformanceStats stats)
        {
            AddBytes(report, stats, AvatarPerformanceCategory.DownloadSize, "SIZE", "Download Size",
                stats.downloadSizeBytes, AnalysisAccuracy.Unavailable,
                "Quick ScanではAssetBundleを生成しないため取得できません。");
            AddFloat(report, stats, AvatarPerformanceCategory.TextureMegabytes, "SIZE", "Texture Memory",
                stats.textureMegabytes, "MB", AnalysisAccuracy.Exact,
                "VRChat SDKによる合計値です。Texture別内訳は推定値です。");
            AddBytes(report, stats, AvatarPerformanceCategory.UncompressedSize, "SIZE", "Uncompressed Size",
                stats.uncompressedSizeBytes, AnalysisAccuracy.Unavailable,
                "Quick ScanではAssetBundleを生成しないため取得できません。");
        }

        private static void AddMeshMetrics(AvatarReport report, AvatarPerformanceStats stats)
        {
            AddInt(report, stats, AvatarPerformanceCategory.PolyCount, "MESH", "Triangles", stats.polyCount);
            AddBounds(report, stats);
            AddInt(report, stats, AvatarPerformanceCategory.SkinnedMeshCount, "MESH", "Skinned Meshes", stats.skinnedMeshCount);
            AddInt(report, stats, AvatarPerformanceCategory.MeshCount, "MESH", "Basic Meshes", stats.meshCount);
            AddInt(report, stats, AvatarPerformanceCategory.MaterialCount, "MESH", "Material Slots", stats.materialCount);
        }

        private static void AddDynamicsMetrics(AvatarReport report, AvatarPerformanceStats stats)
        {
            AddInt(report, stats, AvatarPerformanceCategory.PhysBoneComponentCount, "PHYSBONE", "Components", stats.physBone?.componentCount);
            AddInt(report, stats, AvatarPerformanceCategory.PhysBoneTransformCount, "PHYSBONE", "Affected Transforms", stats.physBone?.transformCount);
            AddInt(report, stats, AvatarPerformanceCategory.PhysBoneColliderCount, "PHYSBONE", "Colliders", stats.physBone?.colliderCount);
            AddInt(report, stats, AvatarPerformanceCategory.PhysBoneCollisionCheckCount, "PHYSBONE", "Collision Checks", stats.physBone?.collisionCheckCount);
            AddInt(report, stats, AvatarPerformanceCategory.ContactCount, "INTERACTION", "Contacts", stats.contactCount);
            AddInt(report, stats, AvatarPerformanceCategory.ConstraintsCount, "CONSTRAINT", "Constraints", stats.constraintsCount);
            AddInt(report, stats, AvatarPerformanceCategory.ConstraintDepth, "CONSTRAINT", "Constraint Depth", stats.constraintDepth);
            AddInt(report, stats, AvatarPerformanceCategory.AnimatorCount, "ANIMATION", "Animators", stats.animatorCount);
            AddInt(report, stats, AvatarPerformanceCategory.BoneCount, "ANIMATION", "Bones", stats.boneCount);
        }

        private static void AddRenderingMetrics(AvatarReport report, AvatarPerformanceStats stats)
        {
            AddInt(report, stats, AvatarPerformanceCategory.LightCount, "RENDERING", "Lights", stats.lightCount);
            AddInt(report, stats, AvatarPerformanceCategory.ParticleSystemCount, "PARTICLE", "Particle Systems", stats.particleSystemCount);
            AddInt(report, stats, AvatarPerformanceCategory.ParticleTotalCount, "PARTICLE", "Maximum Particles", stats.particleTotalCount);
            AddInt(report, stats, AvatarPerformanceCategory.ParticleMaxMeshPolyCount, "PARTICLE", "Mesh Particle Triangles", stats.particleMaxMeshPolyCount);
            AddBool(report, stats, AvatarPerformanceCategory.ParticleTrailsEnabled, "PARTICLE", "Particle Trails", stats.particleTrailsEnabled);
            AddBool(report, stats, AvatarPerformanceCategory.ParticleCollisionEnabled, "PARTICLE", "Particle Collision", stats.particleCollisionEnabled);
            AddInt(report, stats, AvatarPerformanceCategory.TrailRendererCount, "PARTICLE", "Trail Renderers", stats.trailRendererCount);
            AddInt(report, stats, AvatarPerformanceCategory.LineRendererCount, "PARTICLE", "Line Renderers", stats.lineRendererCount);
            AddInt(report, stats, AvatarPerformanceCategory.ClothCount, "CLOTH", "Cloth Components", stats.clothCount);
            AddInt(report, stats, AvatarPerformanceCategory.ClothMaxVertices, "CLOTH", "Cloth Vertices", stats.clothMaxVertices);
            AddInt(report, stats, AvatarPerformanceCategory.PhysicsColliderCount, "PHYSICS", "Colliders", stats.physicsColliderCount);
            AddInt(report, stats, AvatarPerformanceCategory.PhysicsRigidbodyCount, "PHYSICS", "Rigidbodies", stats.physicsRigidbodyCount);
            AddInt(report, stats, AvatarPerformanceCategory.AudioSourceCount, "AUDIO", "Audio Sources", stats.audioSourceCount);
            AddInt(report, stats, AvatarPerformanceCategory.RaycastCount, "OTHER", "Raycasts", stats.raycastCount);
        }

        private static void AddTextureContributors(AvatarReport report, GameObject root)
        {
            var metric = report.Find(AvatarPerformanceCategory.TextureMegabytes);
            if (metric == null) return;

            var usages = new Dictionary<Texture, TextureUsage>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null) continue;
                    var propertyCount = ShaderUtil.GetPropertyCount(material.shader);
                    for (var index = 0; index < propertyCount; index++)
                    {
                        if (ShaderUtil.GetPropertyType(material.shader, index) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                        var texture = material.GetTexture(ShaderUtil.GetPropertyName(material.shader, index));
                        if (texture == null) continue;
                        if (!usages.TryGetValue(texture, out var usage))
                        {
                            usage = new TextureUsage(texture);
                            usages.Add(texture, usage);
                        }

                        usage.Renderers.Add(renderer);
                        usage.Materials.Add(material);
                    }
                }
            }

            foreach (var usage in usages.Values)
            {
                var bytes = Math.Max(0L, Profiler.GetRuntimeMemorySizeLong(usage.Texture));
                var owner = usage.Renderers.OrderBy(renderer => GetPath(renderer.transform, root.transform)).FirstOrDefault();
                metric.Contributors.Add(new Contributor
                {
                    Name = GetAssetDisplayName(usage.Texture),
                    Group = owner == null ? "Other" : GetGroup(owner.transform, root.transform),
                    Value = bytes / 1024d / 1024d,
                    Unit = "MB",
                    Target = usage.Texture,
                    SecondaryTarget = owner,
                    Detail = string.Format(CultureInfo.InvariantCulture, "{0} × {1} · {2} material(s) · {3} renderer(s)",
                        usage.Texture.width, usage.Texture.height, usage.Materials.Count, usage.Renderers.Count)
                });
            }

            metric.Contributors.Sort((left, right) => right.Value.CompareTo(left.Value));
            metric.BreakdownAccuracy = AnalysisAccuracy.Estimated;
        }

        private static string GetAssetDisplayName(UnityEngine.Object asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? asset.name : System.IO.Path.GetFileName(path);
        }

        private static void AddMeshContributors(AvatarReport report, GameObject root)
        {
            var triangleMetric = report.Find(AvatarPerformanceCategory.PolyCount);
            var skinnedMeshMetric = report.Find(AvatarPerformanceCategory.SkinnedMeshCount);
            var materialMetric = report.Find(AvatarPerformanceCategory.MaterialCount);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = GetMesh(renderer);
                if (mesh != null && triangleMetric != null)
                {
                    triangleMetric.Contributors.Add(new Contributor
                    {
                        Name = renderer.name,
                        Group = GetGroup(renderer.transform, root.transform),
                        Value = CountTriangles(mesh),
                        Unit = "triangles",
                        Target = renderer.gameObject,
                        SecondaryTarget = mesh,
                        Detail = mesh.name + " · " + mesh.vertexCount.ToString("N0") + " vertices"
                    });
                }

                if (mesh != null && renderer is SkinnedMeshRenderer skinned && skinnedMeshMetric != null)
                {
                    skinnedMeshMetric.Contributors.Add(new Contributor
                    {
                        Name = renderer.name,
                        Group = GetGroup(renderer.transform, root.transform),
                        Value = CountTriangles(mesh),
                        Unit = "triangles",
                        Target = renderer.gameObject,
                        SecondaryTarget = mesh,
                        Detail = mesh.name + " · " + mesh.vertexCount.ToString("N0") + " vertices · " +
                                 mesh.blendShapeCount.ToString("N0") + " blend shapes · " +
                                 skinned.bones.Count(bone => bone != null).ToString("N0") + " bones"
                    });
                }

                if (materialMetric != null)
                {
                    materialMetric.Contributors.Add(new Contributor
                    {
                        Name = renderer.name,
                        Group = GetGroup(renderer.transform, root.transform),
                        Value = renderer.sharedMaterials.Length,
                        Unit = "slots",
                        Target = renderer.gameObject,
                        SecondaryTarget = renderer.sharedMaterials.FirstOrDefault(material => material != null),
                        Detail = string.Join(", ", renderer.sharedMaterials.Select(material => material == null ? "Missing" : material.name))
                    });
                }
            }

            if (triangleMetric != null)
            {
                triangleMetric.Contributors.Sort((left, right) => right.Value.CompareTo(left.Value));
                triangleMetric.BreakdownAccuracy = AnalysisAccuracy.Exact;
            }

            if (materialMetric != null)
            {
                materialMetric.Contributors.Sort((left, right) => right.Value.CompareTo(left.Value));
                materialMetric.BreakdownAccuracy = AnalysisAccuracy.Exact;
            }

            if (skinnedMeshMetric != null)
            {
                skinnedMeshMetric.Contributors.Sort((left, right) => right.Value.CompareTo(left.Value));
                skinnedMeshMetric.BreakdownAccuracy = AnalysisAccuracy.Exact;
            }
        }

        private static void AddPhysBoneContributors(AvatarReport report, GameObject root)
        {
            var componentMetric = report.Find(AvatarPerformanceCategory.PhysBoneComponentCount);
            var transformMetric = report.Find(AvatarPerformanceCategory.PhysBoneTransformCount);
            var collisionMetric = report.Find(AvatarPerformanceCategory.PhysBoneCollisionCheckCount);
            var skinnedMeshBones = new HashSet<Transform>(
                root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .SelectMany(renderer => renderer.bones ?? Array.Empty<Transform>())
                    .Where(bone => bone != null));

            foreach (var physBone in root.GetComponentsInChildren<VRCPhysBone>(true))
            {
                var affectedTransformList = GetAffectedTransforms(physBone);
                var affectedTransforms = affectedTransformList.Count;
                var meshLinkedTransforms = affectedTransformList.Count(skinnedMeshBones.Contains);
                var isUnused = meshLinkedTransforms == 0;
                var colliderCount = physBone.colliders == null ? 0 : physBone.colliders.Count(collider => collider != null);
                var estimatedChecks = affectedTransforms * colliderCount;
                var group = GetGroup(physBone.transform, root.transform);
                var usageDetail = isUnused
                    ? " · mesh-linked bones: 0 (unused candidate)"
                    : " · mesh-linked bones: " + meshLinkedTransforms;

                if (componentMetric != null)
                {
                    componentMetric.Contributors.Add(new Contributor
                    {
                        Name = physBone.name,
                        Group = group,
                        Value = 1,
                        Unit = "component",
                        Target = physBone.gameObject,
                        SecondaryTarget = physBone,
                        Detail = affectedTransforms + " transforms · " + colliderCount + " colliders" + usageDetail,
                        IsUnusedPhysBone = isUnused
                    });
                }

                if (transformMetric != null)
                {
                    transformMetric.Contributors.Add(new Contributor
                    {
                        Name = physBone.name,
                        Group = group,
                        Value = affectedTransforms,
                        Unit = "transforms",
                        Target = physBone.gameObject,
                        SecondaryTarget = physBone,
                        Detail = "Root: " + (physBone.rootTransform == null ? physBone.transform.name : physBone.rootTransform.name) + usageDetail,
                        IsUnusedPhysBone = isUnused
                    });
                }

                if (collisionMetric != null)
                {
                    collisionMetric.Contributors.Add(new Contributor
                    {
                        Name = physBone.name,
                        Group = group,
                        Value = estimatedChecks,
                        Unit = "checks",
                        Target = physBone.gameObject,
                        SecondaryTarget = physBone,
                        Detail = affectedTransforms + " transforms × " + colliderCount + " colliders (estimated)" + usageDetail,
                        IsUnusedPhysBone = isUnused
                    });
                }
            }

            SortAndSetAccuracy(componentMetric, AnalysisAccuracy.Exact);
            SortAndSetAccuracy(transformMetric, AnalysisAccuracy.Estimated);
            SortAndSetAccuracy(collisionMetric, AnalysisAccuracy.Estimated);
        }

        private static void AddBoundsContributors(AvatarReport report, GameObject root)
        {
            var metric = report.Find(AvatarPerformanceCategory.AABB);
            if (metric == null) return;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var size = renderer.bounds.size;
                var largestAxis = Mathf.Max(size.x, size.y, size.z);
                metric.Contributors.Add(new Contributor
                {
                    Name = renderer.name,
                    Group = GetGroup(renderer.transform, root.transform),
                    Value = largestAxis,
                    Unit = "m",
                    Target = renderer.gameObject,
                    SecondaryTarget = GetMesh(renderer),
                    Detail = string.Format(CultureInfo.InvariantCulture, "{0:0.##} × {1:0.##} × {2:0.##} m", size.x, size.y, size.z)
                });
            }

            SortAndSetAccuracy(metric, AnalysisAccuracy.Estimated);
        }

        private static void AddContactContributors(AvatarReport report, GameObject root)
        {
            var metric = report.Find(AvatarPerformanceCategory.ContactCount);
            if (metric == null) return;

            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                var typeName = behaviour.GetType().Name;
                if (typeName != "VRCContactSender" && typeName != "VRCContactReceiver") continue;
                metric.Contributors.Add(new Contributor
                {
                    Name = behaviour.name,
                    Group = GetGroup(behaviour.transform, root.transform),
                    Value = 1,
                    Unit = "contact",
                    Target = behaviour.gameObject,
                    SecondaryTarget = behaviour,
                    Detail = DescribeContact(behaviour, typeName == "VRCContactSender" ? "Sender" : "Receiver")
                });
            }

            SortAndSetAccuracy(metric, AnalysisAccuracy.Exact);
        }

        private static string DescribeContact(MonoBehaviour contact, string kind)
        {
            var parts = new List<string> { kind };
            AddReflectedValue(parts, contact, "shapeType", "Shape");
            AddReflectedValue(parts, contact, "radius", "Radius");
            AddReflectedValue(parts, contact, "height", "Height");
            AddReflectedValue(parts, contact, "parameter", "Parameter");
            AddReflectedValue(parts, contact, "localOnly", "Local Only");
            AddReflectedValue(parts, contact, "allowSelf", "Allow Self");
            AddReflectedValue(parts, contact, "allowOthers", "Allow Others");
            return string.Join(" · ", parts);
        }

        private static void AddReflectedValue(List<string> parts, object target, string memberName, string label)
        {
            var type = target.GetType();
            object value = null;
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (field != null) value = field.GetValue(target);
            else
            {
                var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (property != null && property.GetIndexParameters().Length == 0) value = property.GetValue(target, null);
            }

            if (value == null || value is string text && string.IsNullOrEmpty(text)) return;
            if (value is float floatValue)
                parts.Add(label + " " + floatValue.ToString("0.###", CultureInfo.InvariantCulture));
            else
                parts.Add(label + " " + value);
        }

        private static void AddConstraintContributors(AvatarReport report, GameObject root)
        {
            var metric = report.Find(AvatarPerformanceCategory.ConstraintsCount);
            var depthMetric = report.Find(AvatarPerformanceCategory.ConstraintDepth);
            if (metric == null && depthMetric == null) return;
            var components = new HashSet<Component>();
            var vrcConstraints = root.GetComponentsInChildren<VRCConstraintBase>(true);

            foreach (var constraint in vrcConstraints)
                components.Add(constraint);
            foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(true))
                if (behaviour is IConstraint) components.Add(behaviour);

            foreach (var component in components)
            {
                if (metric == null) continue;
                var vrcConstraint = component as VRCConstraintBase;
                var detail = component.GetType().Name;
                if (vrcConstraint != null)
                    detail += " · " + vrcConstraint.Sources.Count + " source(s)" +
                              (vrcConstraint.SolveInLocalSpace ? " · Local space" : string.Empty);
                metric.Contributors.Add(new Contributor
                {
                    Name = component.name,
                    Group = GetGroup(component.transform, root.transform),
                    Value = 1,
                    Unit = "constraint",
                    Target = component.gameObject,
                    SecondaryTarget = component,
                    Detail = detail
                });
            }

            SortAndSetAccuracy(metric, AnalysisAccuracy.Exact);
            AddConstraintDepthContributors(depthMetric, vrcConstraints, root);
        }

        private static void AddConstraintDepthContributors(MetricResult metric, VRCConstraintBase[] constraints, GameObject root)
        {
            if (metric == null) return;
            var byTarget = new Dictionary<Transform, VRCConstraintBase>();
            foreach (var constraint in constraints)
            {
                var target = constraint.TargetTransform == null ? constraint.transform : constraint.TargetTransform;
                if (!byTarget.ContainsKey(target)) byTarget.Add(target, constraint);
            }

            var cache = new Dictionary<VRCConstraintBase, int>();
            foreach (var constraint in constraints)
            {
                var visiting = new HashSet<VRCConstraintBase>();
                var depth = CalculateConstraintDepth(constraint, byTarget, cache, visiting);
                metric.Contributors.Add(new Contributor
                {
                    Name = constraint.name,
                    Group = GetGroup(constraint.transform, root.transform),
                    Value = depth,
                    Unit = "depth",
                    Target = constraint.gameObject,
                    SecondaryTarget = constraint,
                    Detail = constraint.GetType().Name + " · " + constraint.Sources.Count + " source(s)"
                });
            }
            SortAndSetAccuracy(metric, AnalysisAccuracy.Estimated);
        }

        private static int CalculateConstraintDepth(VRCConstraintBase constraint,
            Dictionary<Transform, VRCConstraintBase> byTarget,
            Dictionary<VRCConstraintBase, int> cache,
            HashSet<VRCConstraintBase> visiting)
        {
            if (cache.TryGetValue(constraint, out var cached)) return cached;
            if (!visiting.Add(constraint)) return 1;

            var depth = 1;
            foreach (var source in constraint.Sources)
            {
                if (source.SourceTransform == null) continue;
                if (!byTarget.TryGetValue(source.SourceTransform, out var dependency)) continue;
                depth = Math.Max(depth, 1 + CalculateConstraintDepth(dependency, byTarget, cache, visiting));
            }

            visiting.Remove(constraint);
            cache[constraint] = depth;
            return depth;
        }

        private static void AddAnimationContributors(AvatarReport report, GameObject root)
        {
            var animatorMetric = report.Find(AvatarPerformanceCategory.AnimatorCount);
            if (animatorMetric != null)
            {
                foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                {
                    animatorMetric.Contributors.Add(new Contributor
                    {
                        Name = animator.name,
                        Group = GetGroup(animator.transform, root.transform),
                        Value = 1,
                        Unit = "animator",
                        Target = animator.gameObject,
                        SecondaryTarget = animator.runtimeAnimatorController,
                        Detail = animator.runtimeAnimatorController == null ? "No controller" : animator.runtimeAnimatorController.name
                    });
                }
                SortAndSetAccuracy(animatorMetric, AnalysisAccuracy.Exact);
            }

            var boneMetric = report.Find(AvatarPerformanceCategory.BoneCount);
            if (boneMetric == null) return;
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                boneMetric.Contributors.Add(new Contributor
                {
                    Name = renderer.name,
                    Group = GetGroup(renderer.transform, root.transform),
                    Value = renderer.bones.Count(bone => bone != null),
                    Unit = "bone refs",
                    Target = renderer.gameObject,
                    SecondaryTarget = renderer.sharedMesh,
                    Detail = "Renderer bone references; shared bones may appear more than once"
                });
            }
            SortAndSetAccuracy(boneMetric, AnalysisAccuracy.Estimated);
        }

        private static void AddRenderingContributors(AvatarReport report, GameObject root)
        {
            var metric = report.Find(AvatarPerformanceCategory.LightCount);
            if (metric == null) return;
            foreach (var light in root.GetComponentsInChildren<Light>(true))
            {
                metric.Contributors.Add(new Contributor
                {
                    Name = light.name,
                    Group = GetGroup(light.transform, root.transform),
                    Value = 1,
                    Unit = "light",
                    Target = light.gameObject,
                    SecondaryTarget = light,
                    Detail = light.type + " · Range " + light.range.ToString("0.##", CultureInfo.InvariantCulture) + " · Shadows " + light.shadows
                });
            }
            SortAndSetAccuracy(metric, AnalysisAccuracy.Exact);
        }

        private static void AddParticleContributors(AvatarReport report, GameObject root)
        {
            var countMetric = report.Find(AvatarPerformanceCategory.ParticleSystemCount);
            var totalMetric = report.Find(AvatarPerformanceCategory.ParticleTotalCount);
            var meshMetric = report.Find(AvatarPerformanceCategory.ParticleMaxMeshPolyCount);
            var trailsMetric = report.Find(AvatarPerformanceCategory.ParticleTrailsEnabled);
            var collisionMetric = report.Find(AvatarPerformanceCategory.ParticleCollisionEnabled);
            var trailRendererMetric = report.Find(AvatarPerformanceCategory.TrailRendererCount);
            var lineRendererMetric = report.Find(AvatarPerformanceCategory.LineRendererCount);
            var systems = root.GetComponentsInChildren<ParticleSystem>(true);

            foreach (var system in systems)
            {
                var main = system.main;
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                var meshTriangles = renderer == null || renderer.renderMode != ParticleSystemRenderMode.Mesh
                    ? 0L
                    : GetParticleMeshTriangles(renderer);
                var maximumMeshTriangles = meshTriangles * main.maxParticles;
                var detail = renderer == null
                    ? main.maxParticles + " max particles"
                    : renderer.renderMode + " · " + main.maxParticles + " max particles";

                AddComponentContributor(countMetric, system, root, 1, "system", detail);
                AddComponentContributor(totalMetric, system, root, main.maxParticles, "particles", detail);
                if (maximumMeshTriangles > 0)
                    AddComponentContributor(meshMetric, system, root, maximumMeshTriangles, "triangles", detail + " · mesh worst case");
                if (system.trails.enabled)
                    AddComponentContributor(trailsMetric, system, root, 1, "enabled", "Particle trails enabled");
                if (system.collision.enabled)
                    AddComponentContributor(collisionMetric, system, root, 1, "enabled", "Particle collision enabled");
            }

            foreach (var trail in root.GetComponentsInChildren<TrailRenderer>(true))
                AddComponentContributor(trailRendererMetric, trail, root, 1, "renderer",
                    "Time " + trail.time.ToString("0.##", CultureInfo.InvariantCulture) + " · Min Vertex Distance " +
                    trail.minVertexDistance.ToString("0.###", CultureInfo.InvariantCulture));
            foreach (var line in root.GetComponentsInChildren<LineRenderer>(true))
                AddComponentContributor(lineRendererMetric, line, root, 1, "renderer",
                    line.positionCount + " positions · Width " + line.widthMultiplier.ToString("0.###", CultureInfo.InvariantCulture));

            SortAndSetAccuracy(countMetric, AnalysisAccuracy.Exact);
            SortAndSetAccuracy(totalMetric, AnalysisAccuracy.Exact);
            SortAndSetAccuracy(meshMetric, AnalysisAccuracy.Estimated);
            SortAndSetAccuracy(trailsMetric, AnalysisAccuracy.Exact);
            SortAndSetAccuracy(collisionMetric, AnalysisAccuracy.Exact);
            SortAndSetAccuracy(trailRendererMetric, AnalysisAccuracy.Exact);
            SortAndSetAccuracy(lineRendererMetric, AnalysisAccuracy.Exact);
        }

        private static long GetParticleMeshTriangles(ParticleSystemRenderer renderer)
        {
            var meshes = new Mesh[renderer.meshCount];
            var count = renderer.GetMeshes(meshes);
            long maximum = 0;
            for (var index = 0; index < count; index++)
                if (meshes[index] != null) maximum = Math.Max(maximum, CountTriangles(meshes[index]));
            return maximum;
        }

        private static void AddClothContributors(AvatarReport report, GameObject root)
        {
            var countMetric = report.Find(AvatarPerformanceCategory.ClothCount);
            var vertexMetric = report.Find(AvatarPerformanceCategory.ClothMaxVertices);
            foreach (var cloth in root.GetComponentsInChildren<Cloth>(true))
            {
                var renderer = cloth.GetComponent<SkinnedMeshRenderer>();
                var vertices = renderer == null || renderer.sharedMesh == null ? 0 : renderer.sharedMesh.vertexCount;
                AddComponentContributor(countMetric, cloth, root, 1, "cloth", vertices + " vertices");
                AddComponentContributor(vertexMetric, cloth, root, vertices, "vertices", renderer == null ? "Missing SkinnedMeshRenderer" : renderer.name);
            }
            SortAndSetAccuracy(countMetric, AnalysisAccuracy.Exact);
            SortAndSetAccuracy(vertexMetric, AnalysisAccuracy.Exact);
        }

        private static void AddPhysicsContributors(AvatarReport report, GameObject root)
        {
            var colliderMetric = report.Find(AvatarPerformanceCategory.PhysicsColliderCount);
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                AddComponentContributor(colliderMetric, collider, root, 1, "collider", collider.GetType().Name + (collider.isTrigger ? " · Trigger" : string.Empty));
            SortAndSetAccuracy(colliderMetric, AnalysisAccuracy.Exact);

            var rigidbodyMetric = report.Find(AvatarPerformanceCategory.PhysicsRigidbodyCount);
            foreach (var rigidbody in root.GetComponentsInChildren<Rigidbody>(true))
                AddComponentContributor(rigidbodyMetric, rigidbody, root, 1, "rigidbody",
                    "Mass " + rigidbody.mass.ToString("0.##", CultureInfo.InvariantCulture) + (rigidbody.isKinematic ? " · Kinematic" : string.Empty));
            SortAndSetAccuracy(rigidbodyMetric, AnalysisAccuracy.Exact);
        }

        private static void AddAudioContributors(AvatarReport report, GameObject root)
        {
            var metric = report.Find(AvatarPerformanceCategory.AudioSourceCount);
            foreach (var source in root.GetComponentsInChildren<AudioSource>(true))
            {
                AddComponentContributor(metric, source, root, 1, "source",
                    (source.clip == null ? "No clip" : source.clip.name) + " · Spatial Blend " + source.spatialBlend.ToString("0.##", CultureInfo.InvariantCulture));
            }
            SortAndSetAccuracy(metric, AnalysisAccuracy.Exact);
        }

        private static void AddComponentContributor(MetricResult metric, Component component, GameObject root,
            double value, string unit, string detail)
        {
            if (metric == null || component == null) return;
            metric.Contributors.Add(new Contributor
            {
                Name = component.name,
                Group = GetGroup(component.transform, root.transform),
                Value = value,
                Unit = unit,
                Target = component.gameObject,
                SecondaryTarget = component,
                Detail = detail
            });
        }

        private static List<Transform> GetAffectedTransforms(VRCPhysBoneBase physBone)
        {
            var root = physBone.rootTransform == null ? physBone.transform : physBone.rootTransform;
            var ignored = new HashSet<Transform>(physBone.ignoreTransforms ?? new List<Transform>());
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            var affected = new List<Transform>();

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                affected.Add(current);
                for (var index = 0; index < current.childCount; index++)
                {
                    var child = current.GetChild(index);
                    if (!ignored.Contains(child)) queue.Enqueue(child);
                }
            }

            return affected;
        }

        private static void SortAndSetAccuracy(MetricResult metric, AnalysisAccuracy accuracy)
        {
            if (metric == null) return;
            metric.Contributors.Sort((left, right) => right.Value.CompareTo(left.Value));
            metric.BreakdownAccuracy = accuracy;
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter == null ? null : filter.sharedMesh;
        }

        private static long CountTriangles(Mesh mesh)
        {
            long triangles = 0;
            for (var index = 0; index < mesh.subMeshCount; index++)
            {
                if (mesh.GetTopology(index) == MeshTopology.Triangles)
                    triangles += (long)mesh.GetIndexCount(index) / 3L;
            }

            return triangles;
        }

        private static string GetGroup(Transform target, Transform root)
        {
            if (target == null || target == root) return root.name;
            var current = target;
            while (current.parent != null && current.parent != root) current = current.parent;
            return current.name;
        }

        private static string GetPath(Transform target, Transform root)
        {
            var parts = new Stack<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }

        private static void AddBounds(AvatarReport report, AvatarPerformanceStats stats)
        {
            var bounds = stats.aabb;
            var available = bounds.HasValue;
            var largest = available ? Mathf.Max(bounds.Value.size.x, bounds.Value.size.y, bounds.Value.size.z) : 0f;
            report.Metrics.Add(NewMetric(stats, AvatarPerformanceCategory.AABB, "MESH", "Bounds",
                available ? largest.ToString("0.##", CultureInfo.InvariantCulture) + " m" : "Unavailable",
                available ? (double?)largest : null, "m", available ? AnalysisAccuracy.Exact : AnalysisAccuracy.Unavailable));
        }

        private static void AddInt(AvatarReport report, AvatarPerformanceStats stats, AvatarPerformanceCategory category,
            string section, string displayName, int? value)
        {
            report.Metrics.Add(NewMetric(stats, category, section, displayName,
                value.HasValue ? value.Value.ToString("N0") : "Unavailable", value, "count",
                value.HasValue ? AnalysisAccuracy.Exact : AnalysisAccuracy.Unavailable));
        }

        private static void AddFloat(AvatarReport report, AvatarPerformanceStats stats, AvatarPerformanceCategory category,
            string section, string displayName, float? value, string unit, AnalysisAccuracy accuracy, string explanation)
        {
            var metric = NewMetric(stats, category, section, displayName,
                value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + " " + unit : "Unavailable",
                value, unit, value.HasValue ? accuracy : AnalysisAccuracy.Unavailable);
            metric.Explanation = explanation;
            report.Metrics.Add(metric);
        }

        private static void AddBytes(AvatarReport report, AvatarPerformanceStats stats, AvatarPerformanceCategory category,
            string section, string displayName, int? value, AnalysisAccuracy accuracy, string explanation)
        {
            var mb = value.HasValue ? value.Value / 1024d / 1024d : (double?)null;
            var metric = NewMetric(stats, category, section, displayName,
                mb.HasValue ? mb.Value.ToString("0.##", CultureInfo.InvariantCulture) + " MB" : "Unavailable",
                mb, "MB", value.HasValue ? accuracy : AnalysisAccuracy.Unavailable);
            metric.Explanation = explanation;
            report.Metrics.Add(metric);
        }

        private static void AddBool(AvatarReport report, AvatarPerformanceStats stats, AvatarPerformanceCategory category,
            string section, string displayName, bool? value)
        {
            report.Metrics.Add(NewMetric(stats, category, section, displayName,
                value.HasValue ? (value.Value ? "Enabled" : "Disabled") : "Unavailable",
                value.HasValue ? (value.Value ? 1d : 0d) : (double?)null, string.Empty,
                value.HasValue ? AnalysisAccuracy.Exact : AnalysisAccuracy.Unavailable));
        }

        private static MetricResult NewMetric(AvatarPerformanceStats stats, AvatarPerformanceCategory category,
            string section, string displayName, string displayValue, double? value, string unit, AnalysisAccuracy accuracy)
        {
            PerformanceRating rating;
            try
            {
                rating = stats.GetPerformanceRatingForCategory(category);
            }
            catch
            {
                rating = PerformanceRating.None;
            }

            return new MetricResult
            {
                Category = category,
                Section = section,
                DisplayName = displayName,
                DisplayValue = displayValue,
                NumericValue = value,
                Unit = unit,
                Rank = rating,
                Accuracy = accuracy,
                BreakdownAccuracy = AnalysisAccuracy.Unavailable
            };
        }

        private sealed class TextureUsage
        {
            public readonly Texture Texture;
            public readonly HashSet<Renderer> Renderers = new HashSet<Renderer>();
            public readonly HashSet<Material> Materials = new HashSet<Material>();

            public TextureUsage(Texture texture)
            {
                Texture = texture;
            }
        }
    }
}
