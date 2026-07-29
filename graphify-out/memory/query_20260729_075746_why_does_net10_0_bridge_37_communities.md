---
type: "query"
date: "2026-07-29T07:57:46.995571+00:00"
question: "Why does net10.0 bridge 37 communities?"
contributor: "graphify"
source_nodes: ["net10.0", "Cluckwork.Api.csproj"]
---

# Q: Why does net10.0 bridge 37 communities?

## Answer

Resolved: pure structural artifact, no domain meaning. There are 217 net10-related nodes in the graph; the high-betweenness god nodes are per-project packages.lock.json dependency-group keys (tests_cluckwork_api_integrationtests_packages_lock_dependencies_net10_0 at 69 edges, src_cluckwork_api_packages_lock_dependencies_net10_0 at 61, infrastructure at 40, etc.). Every committed NuGet lock file declares a net10.0 framework group, so these nodes tie all lock-file communities together and inflate betweenness centrality. Recommendation: exclude packages.lock.json from code detection in future graphify runs - it would remove this bridge, the NuGet Lock Files community, and a large slice of the 1500 isolated nodes.

## Source Nodes

- net10.0
- Cluckwork.Api.csproj