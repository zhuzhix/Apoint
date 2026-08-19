USE astock_monitor;

-- Tick is an intraday Redis/process-memory concern in V2. This migration is
-- deliberately separate from the canonical-bar migration so fresh installs
-- and upgrades converge on the same no-MySQL-Tick state.
DROP TABLE IF EXISTS quote_tick;

DELETE FROM data_retention_policy WHERE dataset_name='quote_tick';

INSERT INTO schema_migration (version, description)
VALUES ('011', 'retire MySQL Tick storage after V2 cutover')
ON DUPLICATE KEY UPDATE description=VALUES(description);
