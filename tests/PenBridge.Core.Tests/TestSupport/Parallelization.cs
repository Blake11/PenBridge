using Xunit.Sdk;
using Xunit.v3;

// Every test runs in parallel with every other, also within a class (xUnit's default only runs collections, i.e.
// classes, in parallel). The AllocationGroup collection (DisableParallelization) still runs alone, after the
// parallel tests. Network tests stay race-free through the settle API and bounded waits, not by running one at a
// time. Keep the conservative algorithm (at most one running test per core): the aggressive one starts every test at
// once, and under CPU load the network tests then miss their 10 s real-time bounds (core-testing skill, soak).
[assembly: Parallelization(Mode = ParallelMode.All, Algorithm = ParallelAlgorithm.Conservative)]
