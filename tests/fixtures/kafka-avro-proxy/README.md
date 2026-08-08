# Kafka Avro gateway fixture

The disposable fixture creates a real Avro record through a Confluent-compatible Schema Registry
that requires HTTP Basic authentication. It has a Kafka SOCKS5 route and an independent HTTP proxy
for registry traffic. A short-lived fixture CA fronts the Registry with HTTPS; the connector trusts
that CA using Confluent's supported `ssl.ca.location` option, without disabling verification.

The topic is laid out deliberately: **offset 0 is a tombstone** — a null-valued record, what a keyed
topic writes for a delete — and offset 1 is the Avro record. The order matters. A bounded read with a
one-row budget must pass the tombstone and still return the record behind it; if the tombstone were
staged instead, it would spend that budget on a row the connector's own key and not-null gates then
reject, the batch would fail, no offset would commit, and every replay would stall on the same record.
`kafka-console-producer --property null.marker` writes it, rather than the Avro producer, because a
tombstone has no payload to serialise.

The fixed private addresses make Kafka's advertised listener traverse SOCKS. The only host-visible
ports are the two gateway ports and the TLS Registry front end; direct Registry HTTP is not exposed.
All credentials and generated certificates are fixture-only values.
