using System.Linq;
using FastTests;
using FastTests.Server.Basic.Entities;
using Xunit;

namespace SlowTests.MailingList
{
    public class SpecialChars : RavenNewTestBase
    {
        [Fact]
        public void ShouldWork()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Query<User>()
                        .Where(x => x.LastName == "abc&edf")
                        .ToList();
                }
            }
        }
    }
}