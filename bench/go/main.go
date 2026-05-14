// Thin wrapper over ch-go that mirrors our F# bench harness:
//   - reads CLICKHOUSE_PASSWORD from env
//   - same query, same max_block_size setting
//   - verifies row count and sum so neither side can "win" by skipping work
//
// Same shape as reference/ch-go/internal/cmd/ch-bench-numbers/main.go
// but with password support and correctness check.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	"github.com/ClickHouse/ch-go"
	"github.com/ClickHouse/ch-go/proto"
)

func run(ctx context.Context) error {
	var (
		addr      = flag.String("addr", "127.0.0.1:9000", "server address")
		user      = flag.String("user", "default", "user")
		database  = flag.String("database", "default", "database")
		rows      = flag.Int64("rows", 500_000_000, "row limit")
		blockSize = flag.Int("block-size", 65536, "max_block_size setting")
		quiet     = flag.Bool("quiet", false, "suppress OK line")
		pingOnly  = flag.Bool("ping", false, "just ping and exit")
		useLZ4    = flag.Bool("lz4", false, "enable LZ4 compression")
	)
	flag.Parse()

	password := os.Getenv("CLICKHOUSE_PASSWORD")

	opts := ch.Options{
		Address:    *addr,
		User:       *user,
		Database:   *database,
		Password:   password,
		ClientName: "clickhouse/ch-go.bench-numbers-wrapper",
		Settings: []ch.Setting{
			ch.SettingInt("max_block_size", *blockSize),
		},
	}
	if *useLZ4 {
		opts.Compression = ch.CompressionLZ4
	}
	c, err := ch.Dial(ctx, opts)
	if err != nil {
		return fmt.Errorf("dial: %w", err)
	}
	defer c.Close()

	if !*quiet {
		fmt.Fprintf(os.Stderr, "Connected: %s rev=%d\n", c.ServerInfo().Name, c.ServerInfo().Revision)
	}

	if *pingOnly {
		if err := c.Ping(ctx); err != nil {
			return fmt.Errorf("ping: %w", err)
		}
		if !*quiet {
			fmt.Fprintln(os.Stderr, "Pong")
		}
		return nil
	}

	var (
		data        proto.ColUInt64
		totalRow    int64
		totalSum    uint64
		sumDuration time.Duration
	)
	q := ch.Query{
		Body: fmt.Sprintf("SELECT number FROM system.numbers_mt LIMIT %d", *rows),
		Result: proto.Results{
			{Name: "number", Data: &data},
		},
		OnResult: func(ctx context.Context, block proto.Block) error {
			// Sum each block so the JIT/compiler can't elide the decode.
			// Timed separately: the sum is bench scaffolding, not driver
			// work — `driver = ms - sum` is the apples-to-apples figure
			// against the F# bench's identically-measured split.
			t0 := time.Now()
			s := uint64(0)
			for _, v := range data {
				s += v
			}
			totalSum += s
			totalRow += int64(block.Rows)
			sumDuration += time.Since(t0)
			return nil
		},
	}

	start := time.Now()
	if err := c.Do(ctx, q); err != nil {
		return fmt.Errorf("do: %w", err)
	}
	elapsed := time.Since(start)

	expectedRows := *rows
	var expectedSum uint64
	if *rows > 0 {
		n := uint64(*rows)
		expectedSum = n * (n - 1) / 2
	}
	if totalRow != expectedRows {
		return fmt.Errorf("FAIL: got %d rows, expected %d", totalRow, expectedRows)
	}
	if totalSum != expectedSum {
		return fmt.Errorf("FAIL: got sum %d, expected %d", totalSum, expectedSum)
	}

	if !*quiet {
		bytes := totalRow * 8
		gib := float64(bytes) / 1073741824.0
		ms := float64(elapsed.Milliseconds())
		gbPerSec := gib / (ms / 1000.0)
		sumMs := float64(sumDuration.Microseconds()) / 1000.0
		driverMs := ms - sumMs
		fmt.Fprintf(os.Stderr, "OK: %d rows | %.2f GiB | %.0f ms | %.2f GiB/s | sum %.0f | driver %.0f\n",
			totalRow, gib, ms, gbPerSec, sumMs, driverMs)
	}
	return nil
}

func main() {
	if err := run(context.Background()); err != nil {
		fmt.Fprintln(os.Stderr, "ERROR:", err)
		os.Exit(1)
	}
}
