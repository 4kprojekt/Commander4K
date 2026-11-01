# MikroTik RouterOS preview

This external preview reads RouterOS 7 data through its REST API. It is offered
for the detected `MikroTik API` and `MikroTik API-SSL` service entries; those
entries identify the router, while the form asks for the HTTP/HTTPS REST port
(normally 80 or 443).

Enable the RouterOS REST endpoint and use a RouterOS account with read-only
policies. The plugin issues only `GET` requests to `/rest/system/identity`,
`/rest/system/resource`, `/rest/interface`, `/rest/ip/address`,
`/rest/ip/route`, and `/rest/ip/service`. It does not use the binary RouterOS
API protocol on ports 8728/8729 and does not change router configuration.

The password field is masked. Credentials are sent only for the current
preview action and are not saved by the plugin. For HTTPS, the device must
present a certificate trusted by Windows.
