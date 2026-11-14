using Trainer.Web.Release;
using Xunit.Sdk;
using Xunit.v3;

[assembly: AssemblyFixture(typeof(PublishedAppFixture))]

// One published app and one browser for the whole run; timings measured next to other tests would measure them too.
[assembly: Parallelization(Mode = ParallelMode.None)]
