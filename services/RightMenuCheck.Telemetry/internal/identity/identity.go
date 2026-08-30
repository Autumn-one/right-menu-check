package identity

import "strings"

func MachineID(value string) (string, bool) {
	if len(value) != 64 {
		return "", false
	}
	for _, character := range value {
		if !isHex(character) {
			return "", false
		}
	}
	return strings.ToLower(value), true
}

func SessionID(value string) (string, bool) {
	if len(value) != 32 {
		return "", false
	}
	for _, character := range value {
		if !isHex(character) {
			return "", false
		}
	}
	return strings.ToLower(value), true
}

func isHex(character rune) bool {
	return character >= '0' && character <= '9' ||
		character >= 'a' && character <= 'f' ||
		character >= 'A' && character <= 'F'
}
