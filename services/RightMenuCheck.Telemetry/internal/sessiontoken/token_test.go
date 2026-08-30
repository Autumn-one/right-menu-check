package sessiontoken

import (
	"bytes"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"testing"
)

func TestGenerateReturns256BitURLSafeTokenAndDigest(t *testing.T) {
	input := bytes.Repeat([]byte{0x5a}, byteLength)
	token, digest, err := GenerateFrom(bytes.NewReader(input))
	if err != nil {
		t.Fatal(err)
	}
	if token != base64.RawURLEncoding.EncodeToString(input) || len(token) != 43 {
		t.Fatalf("token = %q", token)
	}
	if digest != sha256.Sum256(input) {
		t.Fatal("digest did not hash the raw random bytes")
	}
	parsed, ok := DigestAuthorization("Bearer " + token)
	if !ok || parsed != digest {
		t.Fatal("authorization did not recover the expected digest")
	}
}

func TestGeneratePropagatesEntropyFailure(t *testing.T) {
	if _, _, err := GenerateFrom(errorReader{}); err == nil {
		t.Fatal("GenerateFrom() ignored entropy failure")
	}
}

func TestDigestAuthorizationRejectsMalformedTokens(t *testing.T) {
	valid := base64.RawURLEncoding.EncodeToString(bytes.Repeat([]byte{1}, byteLength))
	for _, value := range []string{
		"",
		valid,
		"Basic " + valid,
		"Bearer",
		"Bearer  " + valid,
		"Bearer " + valid + "=",
		"Bearer " + base64.RawURLEncoding.EncodeToString(bytes.Repeat([]byte{1}, byteLength-1)),
		"Bearer " + valid + " extra",
	} {
		if _, ok := DigestAuthorization(value); ok {
			t.Fatalf("DigestAuthorization(%q) accepted malformed authorization", value)
		}
	}
	if _, ok := DigestAuthorization("bearer " + valid); !ok {
		t.Fatal("authorization scheme should be case-insensitive")
	}
}

type errorReader struct{}

func (errorReader) Read([]byte) (int, error) {
	return 0, errors.New("entropy unavailable")
}
