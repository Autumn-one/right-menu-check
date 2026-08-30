package identity

import (
	"strings"
	"testing"
)

func TestMachineIDMatchesCSharpContract(t *testing.T) {
	upper := strings.Repeat("A1", 32)
	if actual, ok := MachineID(upper); !ok || actual != strings.ToLower(upper) {
		t.Fatalf("MachineID() = %q, %v", actual, ok)
	}

	for _, invalid := range []string{"", strings.Repeat("a", 63), strings.Repeat("a", 65), strings.Repeat("z", 64)} {
		if _, ok := MachineID(invalid); ok {
			t.Fatalf("MachineID(%q) accepted an invalid value", invalid)
		}
	}
}

func TestSessionIDMatchesGuidNContract(t *testing.T) {
	const upper = "00112233445566778899AABBCCDDEEFF"
	if actual, ok := SessionID(upper); !ok || actual != strings.ToLower(upper) {
		t.Fatalf("SessionID() = %q, %v", actual, ok)
	}

	for _, invalid := range []string{
		"00112233-4455-6677-8899-aabbccddeeff",
		"00112233445566778899aabbccddeef",
		"00112233445566778899aabbccddeefg",
	} {
		if _, ok := SessionID(invalid); ok {
			t.Fatalf("SessionID(%q) accepted an invalid value", invalid)
		}
	}
}
