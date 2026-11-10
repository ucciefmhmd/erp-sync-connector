# Security

## Reporting a vulnerability

Please don't open a public issue for security problems. Use GitHub's
[private vulnerability reporting](https://github.com/ucciefmhmd/erp-sync-connector/security/advisories/new) instead.

## Notes for anyone deploying this code

* The API key in `appsettings.Development.json` and `docker-compose.yml` (`dev-local-key-change-me`), and the mock ERP
  credentials, are **demo values**. Generate your own keys and store only their SHA-256 hashes.
* Never commit `Erp:ClientSecret`. Use user secrets locally, and environment variables or a secret store in deployments.
* TLS is expected to terminate at a reverse proxy or ingress in front of the API.
