// One-shot generator: prints CH128 vectors for a handful of test inputs in a
// shape that drops straight into an F# xunit theory.
package main

import (
	"encoding/hex"
	"fmt"
	"strings"

	"github.com/go-faster/city"
)

func main() {
	inputs := []struct {
		label string
		data  []byte
	}{
		{"empty", []byte("")},
		{"a", []byte("a")},
		{"7B", []byte("1234567")},
		{"8B", []byte("12345678")},
		{"15B", []byte("123456789012345")},
		{"16B", []byte("1234567890123456")},
		{"17B", []byte("12345678901234567")},
		{"32B", []byte("12345678901234567890123456789012")},
		{"63B", bytes(63)},
		{"127B", bytes(127)},
		{"128B", bytes(128)},
		{"129B", bytes(129)},
		{"256B", bytes(256)},
		{"1000B", bytes(1000)},
		// One of the CSV vectors:
		{"TSD35", []byte("TSDGQtM27SmjL0naFMqcQ3ETsYKbDbrBeIj")},
	}
	for _, in := range inputs {
		h := city.CH128(in.data)
		fmt.Printf("    [<InlineData(\"%s\", \"%s\", 0x%016xUL, 0x%016xUL)>]\n",
			in.label, hex.EncodeToString(in.data), h.Low, h.High)
	}
	_ = strings.Builder{}
}

// bytes(n) returns a deterministic n-byte slice ("abc...xyz" repeated).
func bytes(n int) []byte {
	out := make([]byte, n)
	for i := 0; i < n; i++ {
		out[i] = byte('a' + (i % 26))
	}
	return out
}
