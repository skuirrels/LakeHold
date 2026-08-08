# Kafka Avro gateway fixture

The disposable fixture creates a real Avro record through a Confluent-compatible Schema Registry
that requires HTTP Basic authentication. It has a Kafka SOCKS5 route and an independent HTTP proxy
for registry traffic. A short-lived fixture CA fronts the Registry with HTTPS; the connector trusts
that CA using Confluent's supported `ssl.ca.location` option, without disabling verification.

The fixed private addresses make Kafka's advertised listener traverse SOCKS. The only host-visible
ports are the two gateway ports and the TLS Registry front end; direct Registry HTTP is not exposed.
All credentials and generated certificates are fixture-only values.
