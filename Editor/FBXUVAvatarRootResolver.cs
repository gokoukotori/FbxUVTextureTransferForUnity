using System;
using System.Collections.Generic;
using nadena.dev.ndmf.runtime;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal enum FBXUVAvatarRootResolutionStatus
    {
        Resolved,
        DescriptorNotFound,
        NestedAvatarDescriptors,
        NdmfAvatarRootNotFound,
        NdmfAvatarRootMismatch
    }

    internal readonly struct FBXUVAvatarRootResolution
    {
        internal FBXUVAvatarRootResolution(
            FBXUVAvatarRootResolutionStatus status,
            VRCAvatarDescriptor descriptor,
            Transform ndmfAvatarRoot)
        {
            Status = status;
            Descriptor = descriptor;
            NdmfAvatarRoot = ndmfAvatarRoot;
        }

        internal FBXUVAvatarRootResolutionStatus Status { get; }
        internal VRCAvatarDescriptor Descriptor { get; }
        internal Transform NdmfAvatarRoot { get; }
        internal bool IsResolved { get { return Status == FBXUVAvatarRootResolutionStatus.Resolved; } }
        internal GameObject AvatarRoot { get { return IsResolved ? Descriptor.gameObject : null; } }
    }

    internal static class FBXUVAvatarRootResolver
    {
        internal static bool TryResolve(
            FBXUVTextureTransferLayer layer,
            out GameObject avatarRoot,
            out string error)
        {
            return TryResolve((Component)layer, out avatarRoot, out error);
        }

        internal static bool TryResolve(
            Component layer,
            out GameObject avatarRoot,
            out string error)
        {
            avatarRoot = null;
            if (layer == null)
            {
                error = "FBX UV Texture Transfer Layer がありません。";
                return false;
            }

            var resolution = Resolve(layer);
            if (resolution.IsResolved)
            {
                avatarRoot = resolution.AvatarRoot;
                error = string.Empty;
                return true;
            }

            error = ErrorMessage(resolution.Status);
            return false;
        }

        internal static FBXUVAvatarRootResolution Resolve(Component context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var descriptors = FindDescriptorsInParents(context.transform);
            var ndmfAvatarRoot = RuntimeUtil.FindAvatarInParents(context.transform);
            if (descriptors.Count == 0)
            {
                return new FBXUVAvatarRootResolution(
                    FBXUVAvatarRootResolutionStatus.DescriptorNotFound,
                    null,
                    ndmfAvatarRoot);
            }

            var descriptor = descriptors[0];
            if (descriptors.Count > 1)
            {
                return new FBXUVAvatarRootResolution(
                    FBXUVAvatarRootResolutionStatus.NestedAvatarDescriptors,
                    descriptor,
                    ndmfAvatarRoot);
            }

            if (ndmfAvatarRoot == null)
            {
                return new FBXUVAvatarRootResolution(
                    FBXUVAvatarRootResolutionStatus.NdmfAvatarRootNotFound,
                    descriptor,
                    null);
            }

            if (ndmfAvatarRoot != descriptor.transform)
            {
                return new FBXUVAvatarRootResolution(
                    FBXUVAvatarRootResolutionStatus.NdmfAvatarRootMismatch,
                    descriptor,
                    ndmfAvatarRoot);
            }

            return new FBXUVAvatarRootResolution(
                FBXUVAvatarRootResolutionStatus.Resolved,
                descriptor,
                ndmfAvatarRoot);
        }

        private static List<VRCAvatarDescriptor> FindDescriptorsInParents(Transform context)
        {
            var descriptors = new List<VRCAvatarDescriptor>();
            var current = context;
            while (current != null)
            {
                descriptors.AddRange(current.GetComponents<VRCAvatarDescriptor>());
                current = current.parent;
            }

            return descriptors;
        }

        private static string ErrorMessage(FBXUVAvatarRootResolutionStatus status)
        {
            switch (status)
            {
                case FBXUVAvatarRootResolutionStatus.DescriptorNotFound:
                    return "親階層に VRCAvatarDescriptor がありません。";
                case FBXUVAvatarRootResolutionStatus.NestedAvatarDescriptors:
                    return "親階層に複数の VRCAvatarDescriptor があります。";
                case FBXUVAvatarRootResolutionStatus.NdmfAvatarRootNotFound:
                    return "NDMF が Avatar Root を特定できません。";
                case FBXUVAvatarRootResolutionStatus.NdmfAvatarRootMismatch:
                    return "VRCAvatarDescriptor の GameObject と NDMF Avatar Root が一致しません。";
                default:
                    return "Avatar Root を特定できません。";
            }
        }
    }
}
