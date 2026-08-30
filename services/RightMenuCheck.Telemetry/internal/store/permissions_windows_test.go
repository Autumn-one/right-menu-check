//go:build windows

package store

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"testing"
	"unsafe"

	"golang.org/x/sys/windows"
)

func TestSecureStorageFilePreservesNotExistClassification(t *testing.T) {
	err := secureStorageFile(filepath.Join(t.TempDir(), "missing.db-wal"))
	if !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("secureStorageFile() error = %v; want os.ErrNotExist", err)
	}
}

func TestSecureStorageACLRestrictsDirectoryAndFile(t *testing.T) {
	directory := filepath.Join(t.TempDir(), "state")
	if err := os.Mkdir(directory, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := secureStorageDirectory(directory); err != nil {
		t.Fatal(err)
	}
	trustees, err := storageTrustees()
	if err != nil {
		t.Fatal(err)
	}
	if err := verifyStorageACL(directory, true, trustees); err != nil {
		t.Fatal(err)
	}

	file := filepath.Join(directory, "telemetry.db")
	if err := os.WriteFile(file, []byte("test"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := secureStorageFile(file); err != nil {
		t.Fatal(err)
	}
	if err := verifyStorageACL(file, false, trustees); err != nil {
		t.Fatal(err)
	}

	authenticatedUsers, err := windows.CreateWellKnownSid(windows.WinAuthenticatedUserSid)
	if err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{directory, file} {
		present, err := storageACLContainsSID(path, authenticatedUsers)
		if err != nil {
			t.Fatal(err)
		}
		if present {
			t.Fatalf("%s grants an ACE to Authenticated Users", path)
		}
	}
}

func TestVerifyStorageACLRejectsUnexpectedOrdinarySID(t *testing.T) {
	file := filepath.Join(t.TempDir(), "telemetry.db")
	if err := os.WriteFile(file, []byte("test"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := secureStorageFile(file); err != nil {
		t.Fatal(err)
	}
	descriptor, err := windows.GetNamedSecurityInfo(
		file,
		windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION,
	)
	if err != nil {
		t.Fatal(err)
	}
	dacl, _, err := descriptor.DACL()
	if err != nil {
		t.Fatal(err)
	}
	authenticatedUsers, err := windows.CreateWellKnownSid(windows.WinAuthenticatedUserSid)
	if err != nil {
		t.Fatal(err)
	}
	acl, err := windows.ACLFromEntries([]windows.EXPLICIT_ACCESS{{
		AccessPermissions: windows.GENERIC_READ,
		AccessMode:        windows.GRANT_ACCESS,
		Trustee: windows.TRUSTEE{
			TrusteeForm:  windows.TRUSTEE_IS_SID,
			TrusteeType:  windows.TRUSTEE_IS_WELL_KNOWN_GROUP,
			TrusteeValue: windows.TrusteeValueFromSID(authenticatedUsers),
		},
	}}, dacl)
	if err != nil {
		t.Fatal(err)
	}
	if err := windows.SetNamedSecurityInfo(
		file,
		windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION,
		nil,
		nil,
		acl,
		nil,
	); err != nil {
		t.Fatal(err)
	}
	trustees, err := storageTrustees()
	if err != nil {
		t.Fatal(err)
	}
	if err := verifyStorageACL(file, false, trustees); err == nil {
		t.Fatal("verifyStorageACL() accepted an unexpected ordinary-user ACE")
	}
}

func TestOpenSecuresDatabaseAndExistingSidecars(t *testing.T) {
	directory := filepath.Join(t.TempDir(), "state")
	path := filepath.Join(directory, "telemetry.db")
	dataStore, err := Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	defer dataStore.Close()

	trustees, err := storageTrustees()
	if err != nil {
		t.Fatal(err)
	}
	if err := verifyStorageACL(directory, true, trustees); err != nil {
		t.Fatal(err)
	}
	if err := verifyStorageACL(path, false, trustees); err != nil {
		t.Fatal(err)
	}
	for _, sidecar := range []string{path + "-wal", path + "-shm"} {
		if _, err := os.Stat(sidecar); err == nil {
			if err := verifyStorageACL(sidecar, false, trustees); err != nil {
				t.Fatal(err)
			}
		} else if !os.IsNotExist(err) {
			t.Fatal(err)
		}
	}
}

func storageACLContainsSID(path string, wanted *windows.SID) (bool, error) {
	descriptor, err := windows.GetNamedSecurityInfo(
		path,
		windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION,
	)
	if err != nil {
		return false, err
	}
	dacl, _, err := descriptor.DACL()
	if err != nil {
		return false, err
	}
	for index := uint16(0); index < dacl.AceCount; index++ {
		var ace *windows.ACCESS_ALLOWED_ACE
		if err := windows.GetAce(dacl, uint32(index), &ace); err != nil {
			return false, err
		}
		if ace.Header.AceType != windows.ACCESS_ALLOWED_ACE_TYPE {
			continue
		}
		sid := (*windows.SID)(unsafe.Pointer(&ace.SidStart))
		if sid.Equals(wanted) {
			return true, nil
		}
	}
	return false, nil
}
