using System;
using JetBrains.Annotations;

namespace Raven.Server.Routing
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    [MeansImplicitUse]
    public class RavenActionAttribute : Attribute
    {
        public bool IsDebugInformationEndpoint { get; set; }

        public string Path { get; }

        public string Method { get; }


        public AuthorizationStatus RequiredAuthorization { get; set; }

        public bool SkipUsagesCount { get; set; }

        public RavenActionAttribute(string path, string method, AuthorizationStatus requireAuth, bool isDebugInformationEndpoint = false)
        {
            Path = path;
            Method = method;
            IsDebugInformationEndpoint = isDebugInformationEndpoint;
            RequiredAuthorization = requireAuth;
        }
    }

    public enum AuthorizationStatus
    {
        ClusterAdmin,
        Operator,
        DatabaseAdmin,
        ValidUser,
        UnauthenticatedClients
    }
}
