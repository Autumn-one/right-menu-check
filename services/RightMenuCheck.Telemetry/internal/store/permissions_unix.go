//go:build !windows

package store

import "os"

func secureStorageDirectory(path string) error {
	return os.Chmod(path, 0o700)
}

func secureStorageFile(path string) error {
	return os.Chmod(path, 0o600)
}
