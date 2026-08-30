//go:build windows

package store

import (
	"errors"
	"fmt"
	"unsafe"

	"golang.org/x/sys/windows"
)

const fileAllAccess = windows.STANDARD_RIGHTS_REQUIRED | windows.SYNCHRONIZE | 0x1ff

type storageTrustee struct {
	sid         *windows.SID
	trusteeType windows.TRUSTEE_TYPE
}

func secureStorageDirectory(path string) error {
	return applyAndVerifyStorageACL(path, true)
}

func secureStorageFile(path string) error {
	return applyAndVerifyStorageACL(path, false)
}

func applyAndVerifyStorageACL(path string, directory bool) error {
	trustees, err := storageTrustees()
	if err != nil {
		return err
	}

	inheritance := uint32(windows.NO_INHERITANCE)
	if directory {
		inheritance = windows.OBJECT_INHERIT_ACE | windows.CONTAINER_INHERIT_ACE
	}
	entries := make([]windows.EXPLICIT_ACCESS, 0, len(trustees))
	for _, trustee := range trustees {
		entries = append(entries, windows.EXPLICIT_ACCESS{
			AccessPermissions: windows.GENERIC_ALL,
			AccessMode:        windows.SET_ACCESS,
			Inheritance:       inheritance,
			Trustee: windows.TRUSTEE{
				TrusteeForm:  windows.TRUSTEE_IS_SID,
				TrusteeType:  trustee.trusteeType,
				TrusteeValue: windows.TrusteeValueFromSID(trustee.sid),
			},
		})
	}
	acl, err := windows.ACLFromEntries(entries, nil)
	if err != nil {
		return fmt.Errorf("build protected storage ACL: %w", err)
	}
	if err := windows.SetNamedSecurityInfo(
		path,
		windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION,
		nil,
		nil,
		acl,
		nil,
	); err != nil {
		return fmt.Errorf("set protected storage ACL: %w", err)
	}
	return verifyStorageACL(path, directory, trustees)
}

func verifyStorageACL(path string, directory bool, trustees []storageTrustee) error {
	descriptor, err := windows.GetNamedSecurityInfo(
		path,
		windows.SE_FILE_OBJECT,
		windows.OWNER_SECURITY_INFORMATION|windows.DACL_SECURITY_INFORMATION,
	)
	if err != nil {
		return fmt.Errorf("read storage security descriptor: %w", err)
	}
	if !descriptor.IsValid() {
		return errors.New("storage security descriptor is invalid")
	}
	control, _, err := descriptor.Control()
	if err != nil {
		return fmt.Errorf("read storage security descriptor control: %w", err)
	}
	if control&windows.SE_DACL_PROTECTED == 0 {
		return errors.New("storage DACL is not protected from inheritance")
	}
	owner, _, err := descriptor.Owner()
	if err != nil {
		return fmt.Errorf("read storage owner: %w", err)
	}
	if !containsTrustee(trustees, owner) {
		return fmt.Errorf("storage owner %s is outside the allowed identities", owner.String())
	}

	dacl, defaulted, err := descriptor.DACL()
	if err != nil {
		return fmt.Errorf("read storage DACL: %w", err)
	}
	if dacl == nil {
		return errors.New("storage DACL is missing")
	}
	if defaulted {
		return errors.New("storage DACL is marked as defaulted")
	}
	type coverage struct {
		objectAccess         bool
		fileInheritance      bool
		directoryInheritance bool
	}
	covered := make([]coverage, len(trustees))
	for index := uint16(0); index < dacl.AceCount; index++ {
		var ace *windows.ACCESS_ALLOWED_ACE
		if err := windows.GetAce(dacl, uint32(index), &ace); err != nil {
			return fmt.Errorf("read storage ACE %d: %w", index, err)
		}
		if ace.Header.AceType != windows.ACCESS_ALLOWED_ACE_TYPE {
			return fmt.Errorf("storage ACE %d is not an allow ACE", index)
		}
		if ace.Header.AceFlags&windows.INHERITED_ACE != 0 {
			return fmt.Errorf("storage ACE %d is inherited", index)
		}
		if !grantsFullControl(ace.Mask) {
			return fmt.Errorf("storage ACE %d does not grant full control", index)
		}

		sid := (*windows.SID)(unsafe.Pointer(&ace.SidStart))
		trusteeIndex := findTrustee(trustees, sid)
		if trusteeIndex < 0 {
			return fmt.Errorf("storage ACE %d grants access to unexpected SID %s", index, sid.String())
		}

		inheritance := ace.Header.AceFlags & windows.VALID_INHERIT_FLAGS
		if !directory {
			if inheritance != windows.NO_INHERITANCE {
				return fmt.Errorf("storage file ACE %d has inheritance flags", index)
			}
			covered[trusteeIndex].objectAccess = true
			continue
		}
		if inheritance & ^uint8(
			windows.OBJECT_INHERIT_ACE|windows.CONTAINER_INHERIT_ACE|windows.INHERIT_ONLY_ACE) != 0 {
			return fmt.Errorf("storage directory ACE %d has unexpected inheritance flags", index)
		}
		if inheritance&windows.INHERIT_ONLY_ACE == 0 {
			covered[trusteeIndex].objectAccess = true
		}
		if inheritance&windows.OBJECT_INHERIT_ACE != 0 {
			covered[trusteeIndex].fileInheritance = true
		}
		if inheritance&windows.CONTAINER_INHERIT_ACE != 0 {
			covered[trusteeIndex].directoryInheritance = true
		}
	}
	for index, item := range covered {
		if !item.objectAccess {
			return fmt.Errorf("storage DACL does not grant object access to SID %s", trustees[index].sid.String())
		}
		if directory && (!item.fileInheritance || !item.directoryInheritance) {
			return fmt.Errorf("storage DACL does not grant child inheritance to SID %s", trustees[index].sid.String())
		}
	}
	return nil
}

func storageTrustees() ([]storageTrustee, error) {
	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		return nil, fmt.Errorf("read current process user SID: %w", err)
	}
	system, err := windows.CreateWellKnownSid(windows.WinLocalSystemSid)
	if err != nil {
		return nil, fmt.Errorf("create LocalSystem SID: %w", err)
	}
	administrators, err := windows.CreateWellKnownSid(windows.WinBuiltinAdministratorsSid)
	if err != nil {
		return nil, fmt.Errorf("create Administrators SID: %w", err)
	}

	result := make([]storageTrustee, 0, 3)
	for _, candidate := range []storageTrustee{
		{sid: user.User.Sid, trusteeType: windows.TRUSTEE_IS_USER},
		{sid: system, trusteeType: windows.TRUSTEE_IS_USER},
		{sid: administrators, trusteeType: windows.TRUSTEE_IS_GROUP},
	} {
		if !containsTrustee(result, candidate.sid) {
			result = append(result, candidate)
		}
	}
	return result, nil
}

func containsTrustee(trustees []storageTrustee, sid *windows.SID) bool {
	return findTrustee(trustees, sid) >= 0
}

func findTrustee(trustees []storageTrustee, sid *windows.SID) int {
	for index, trustee := range trustees {
		if trustee.sid.Equals(sid) {
			return index
		}
	}
	return -1
}

func grantsFullControl(mask windows.ACCESS_MASK) bool {
	return mask&windows.GENERIC_ALL != 0 || mask&fileAllAccess == fileAllAccess
}
