PLACEHOLDER LAMBDA PACKAGE - NOT DEPLOYABLE

This directory is used as the Lambda code asset only when the real backend
artifact directory (apiAssetPath / migrationAssetPath) does not exist, so that
`cdk synth` and the unit tests work on a machine that has not built the backend.

A function deployed with this package cannot start. Real deployments must pass
`-c requireArtifacts=true`, which turns a missing artifact into a hard error.
