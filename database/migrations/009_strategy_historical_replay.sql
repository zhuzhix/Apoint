USE astock_monitor;

CREATE TABLE IF NOT EXISTS strategy_replay_run (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_key VARCHAR(190) NOT NULL,
    algorithm_version VARCHAR(32) NOT NULL,
    date_from DATE NOT NULL,
    date_to DATE NOT NULL,
    train_end_date DATE NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'running',
    requested_symbols INT NOT NULL DEFAULT 0,
    completed_symbols INT NOT NULL DEFAULT 0,
    evaluated_points BIGINT NOT NULL DEFAULT 0,
    qualified_observations BIGINT NOT NULL DEFAULT 0,
    daily_signals INT NOT NULL DEFAULT 0,
    error_count INT NOT NULL DEFAULT 0,
    options_json JSON NOT NULL,
    data_limitations JSON NULL,
    error_message VARCHAR(2000) NULL,
    started_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    finished_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_strategy_replay_run_key (run_key),
    KEY ix_strategy_replay_run_status (status, started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_replay_symbol (
    run_id BIGINT NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'pending',
    evaluated_points BIGINT NOT NULL DEFAULT 0,
    qualified_observations BIGINT NOT NULL DEFAULT 0,
    daily_signals INT NOT NULL DEFAULT 0,
    error_message VARCHAR(2000) NULL,
    started_at DATETIME(6) NULL,
    finished_at DATETIME(6) NULL,
    PRIMARY KEY (run_id, symbol),
    KEY ix_strategy_replay_symbol_status (run_id, status, symbol)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_replay_signal (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_id BIGINT NOT NULL,
    strategy_code VARCHAR(96) NOT NULL,
    strategy_version VARCHAR(32) NOT NULL,
    symbol VARCHAR(32) NOT NULL,
    trading_date DATE NOT NULL,
    threshold_score DECIMAL(8,2) NOT NULL,
    observed_at DATETIME(6) NOT NULL,
    observed_score DECIMAL(8,2) NOT NULL,
    action VARCHAR(24) NOT NULL,
    confidence VARCHAR(16) NOT NULL,
    hit_price DECIMAL(20,6) NOT NULL,
    stop_reference DECIMAL(20,6) NULL,
    target_reference DECIMAL(20,6) NULL,
    passed_conditions JSON NOT NULL,
    feature_snapshot JSON NOT NULL,
    parameter_snapshot JSON NOT NULL,
    source_watermark VARCHAR(255) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_strategy_replay_signal
        (run_id, strategy_code, symbol, trading_date, threshold_score),
    KEY ix_strategy_replay_signal_strategy
        (run_id, strategy_code, threshold_score, trading_date),
    KEY ix_strategy_replay_signal_symbol (run_id, symbol, trading_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_replay_outcome (
    signal_id BIGINT NOT NULL,
    d1_date DATE NULL,
    d1_return_pct DECIMAL(12,6) NULL,
    d3_date DATE NULL,
    d3_return_pct DECIMAL(12,6) NULL,
    d5_date DATE NULL,
    d5_return_pct DECIMAL(12,6) NULL,
    w1_date DATE NULL,
    w1_return_pct DECIMAL(12,6) NULL,
    mfe5_pct DECIMAL(12,6) NULL,
    mae5_pct DECIMAL(12,6) NULL,
    is_complete BOOLEAN NOT NULL DEFAULT FALSE,
    calculated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (signal_id),
    KEY ix_strategy_replay_outcome_complete (is_complete, calculated_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS strategy_calibration_result (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_id BIGINT NOT NULL,
    strategy_code VARCHAR(96) NOT NULL,
    threshold_score DECIMAL(8,2) NOT NULL,
    sample_segment VARCHAR(16) NOT NULL,
    sample_count INT NOT NULL,
    d1_win_rate DECIMAL(10,6) NULL,
    d1_avg_return DECIMAL(12,6) NULL,
    d3_win_rate DECIMAL(10,6) NULL,
    d3_avg_return DECIMAL(12,6) NULL,
    d5_win_rate DECIMAL(10,6) NULL,
    d5_avg_return DECIMAL(12,6) NULL,
    w1_win_rate DECIMAL(10,6) NULL,
    w1_avg_return DECIMAL(12,6) NULL,
    mfe5_avg DECIMAL(12,6) NULL,
    mae5_avg DECIMAL(12,6) NULL,
    objective_score DECIMAL(12,6) NULL,
    recommended BOOLEAN NOT NULL DEFAULT FALSE,
    recommendation_reason VARCHAR(1000) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uk_strategy_calibration (run_id, strategy_code, threshold_score, sample_segment),
    KEY ix_strategy_calibration_recommended (run_id, recommended, strategy_code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO data_retention_policy
    (dataset_name, retention_mode, cutoff_rule, archive_before_purge)
VALUES
    ('strategy_replay_run', 'keep_forever', 'Keep replay provenance and calibration results', TRUE),
    ('strategy_replay_signal', 'keep_forever', 'Keep point-in-time strategy evidence', TRUE)
ON DUPLICATE KEY UPDATE
    retention_mode=VALUES(retention_mode), cutoff_rule=VALUES(cutoff_rule),
    archive_before_purge=VALUES(archive_before_purge);

INSERT INTO schema_migration (version, description)
VALUES ('009', 'point-in-time strategy replay, forward outcomes and threshold calibration')
ON DUPLICATE KEY UPDATE description=VALUES(description);
