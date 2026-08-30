package sessiontoken

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"io"
	"strings"
)

const byteLength = 32

type Digest [sha256.Size]byte

func Generate() (string, Digest, error) {
	return GenerateFrom(rand.Reader)
}

func GenerateFrom(reader io.Reader) (string, Digest, error) {
	value := make([]byte, byteLength)
	if _, err := io.ReadFull(reader, value); err != nil {
		return "", Digest{}, err
	}
	return base64.RawURLEncoding.EncodeToString(value), sha256.Sum256(value), nil
}

func DigestAuthorization(header string) (Digest, bool) {
	scheme, encoded, ok := strings.Cut(header, " ")
	if !ok || !strings.EqualFold(scheme, "Bearer") || encoded == "" ||
		strings.ContainsAny(encoded, " \t\r\n") {
		return Digest{}, false
	}
	value, err := base64.RawURLEncoding.DecodeString(encoded)
	if err != nil || len(value) != byteLength {
		return Digest{}, false
	}
	return sha256.Sum256(value), true
}
