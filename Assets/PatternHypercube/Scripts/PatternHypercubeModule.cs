using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using UnityEngine;
using KeepCoding;

public class PatternHypercubeModule : ModuleScript {
	public const float ANIMATION_DURATION = 0.5f;
	public const float MOUSE_MAX_ROTATION_ANGLE = Mathf.PI / 6;
	public const float MOUSE_EMULATOR_SPEED = 0.25f;
	public static readonly string[] AXLES_NAMES = new[] { "X", "Y", "Z", "W" };
	public static readonly Dictionary<char, int> AXIS_TO_INDEX = new Dictionary<char, int> { { 'x', 0 }, { 'y', 1 }, { 'z', 2 }, { 'w', 3 } };

	public readonly string TwitchHelpMessage = new[] {
		"\"!{0} 3d\" or \"!{0} 4d\" - change mode",
		"\"!{0} next\" or \"!{0} prev\" - change hyperface/pattern",
		"\"!{0} XY\" - rotate hypercube/hyperface",
		"\"!{0} submit\" - place pattern on selected hyperface",
		"All of the specified commands can be chained via spaces."
	}.Join(" | ");

	public Transform HypercubeContainer;
	public Transform RightButtonsContainer;
	public Transform TopButtonsContainer;
	public KMSelectable Selectable;
	public KMAudio Audio;
	public HyperfaceComponent HyperfacePrefab;
	public ButtonComponent ButtonPrefab;

	public bool TwitchPlaysActive;

	private readonly string[] ButtonPressSounds = new[] { "ButtonPress1", "ButtonPress2" };

	private bool _solved = false;
	private bool _animation = false;
	private bool _4dMode = true;
	private int _selectedPrimaryRotationAxisIndex = -1;
	private int _animationPrimaryAxisIndex = 0;
	private int _animationSecondaryAxisIndex = 0;
	private int _selectedHyperfaceIndex = 0;
	private int _selectedPatternIndex = 0;
	private int[] _shuffledHyperfacesIndices;
	private int[] _shuffledPatternsIndices;
	private float _animationEndsAt = -1f;
	private _4D.Matrix5x5Int _hypercubeRotation = _4D.Matrix5x5Int.IDENTITY;
	private _4D.Matrix5x5Int _hypercubeRotationAfterAnimation = _4D.Matrix5x5Int.IDENTITY;
	private HyperfaceComponent[] _hyperfaces;
	private ButtonComponent _3dModeButton;
	private ButtonComponent _4dModeButton;
	private ButtonComponent _prevButton;
	private ButtonComponent _nextButton;
	private ButtonComponent _submitButton;
	private ButtonComponent[][] _rotationButtons;
	private Vector2[] _mouseEmulatorQueue = new[] { Vector2.zero };
	private float _mouseEmulatorTime = 10f;

	private int _realSelectedHyperfaceIndex { get { return _shuffledHyperfacesIndices[_selectedHyperfaceIndex]; } }
	private int _realSelectedPatternIndex { get { return _shuffledPatternsIndices[_selectedPatternIndex]; } }

	private void Start() {
		CreateHypercube();
		SetExpectedSymbols();
		int placedHyperfaceIndex = PlaceRandomHyperface();
		CreateButtons();
		_shuffledHyperfacesIndices = Enumerable.Range(0, 8).OrderBy(_ => Random.value).ToArray();
		_shuffledPatternsIndices = Enumerable.Range(0, 8).OrderBy(_ => Random.value).ToArray();
		_selectedHyperfaceIndex = Enumerable.Range(0, 8).Where(i => i != _shuffledHyperfacesIndices[placedHyperfaceIndex]).PickRandom();
		_selectedPatternIndex = Enumerable.Range(0, 8).Where(i => i != _shuffledPatternsIndices[placedHyperfaceIndex]).PickRandom();
		_hyperfaces[_realSelectedHyperfaceIndex].Symbols = _hyperfaces[_realSelectedPatternIndex].ExpectedSymbols;
		for (int i = 0; i < 8; i++) {
			if (i == placedHyperfaceIndex) continue;
			_hyperfaces[i].SelfRotation = _4D.Matrix5x5Int.GetRandomXYZRotation();
		}
		_hypercubeRotation = _4D.Matrix5x5Int.GetRandomRotation();
	}

	private void Update() {
		_4D.Matrix5x5 projection = _4D.Matrix5x5.Perpective(120f, 0.1f, 100f);
		_4D.Matrix5x5 selfRotation = new _4D.Matrix5x5(_hypercubeRotation);
		if (_animation) {
			if (Time.time >= _animationEndsAt) {
				_animation = false;
				if (_4dMode) {
					_hypercubeRotation = _hypercubeRotationAfterAnimation;
					selfRotation = new _4D.Matrix5x5(_hypercubeRotation);
				} else {
					_hyperfaces[_realSelectedHyperfaceIndex].SelfRotation = _hypercubeRotationAfterAnimation;
					_hyperfaces[_realSelectedHyperfaceIndex].AnimationRotation = _4D.Matrix5x5.IDENTITY;
				}
				_rotationButtons[1][_animationSecondaryAxisIndex].LabelColor = Color.gray;
			} else {
				float passedTime = ANIMATION_DURATION - (_animationEndsAt - Time.time);
				float angle = passedTime / ANIMATION_DURATION * Mathf.PI / 2f;
				_4D.Matrix5x5 rotation = _4D.Matrix5x5.Rotation(_animationPrimaryAxisIndex, _animationSecondaryAxisIndex, angle);
				if (_4dMode) selfRotation = selfRotation * rotation;
				else _hyperfaces[_realSelectedHyperfaceIndex].AnimationRotation = rotation;
			}
		}
		_4D.Matrix5x5 mouseRotation = TwitchPlaysActive ? MouseEmulator(selfRotation) : RotateUsingMouse(selfRotation);
		_4D.Matrix5x5 selfTransform = mouseRotation * _4D.Matrix5x5.Translation(5f * _4D.Utils.Vector4Kata) * projection;
		foreach (HyperfaceComponent face in _hyperfaces) face.Transform(selfTransform);
	}

	public IEnumerator ProcessTwitchCommand(string command) {
		command = command.Trim().ToLower();
		string[] cmdSplit = command.Trim().ToLower().Split();
		bool simulated4DMode = _4dMode;
		List<ButtonComponent> btnsSelectable = new List<ButtonComponent>();
		for (int x = 0; x < cmdSplit.Length; x++) {
			string curCmdPart = cmdSplit[x];
			if (Regex.IsMatch(curCmdPart, @"^[xyzw]{2}$") && curCmdPart[0] != curCmdPart[1]) {
				if (!simulated4DMode && curCmdPart.Contains('w')) {
					yield return string.Format("sendtochaterror {0}, !{1} unable to use rotation over \"W\" in 3D mode after processing {2} command(s)", "{0}", "{1}", x);
					yield break;
				}
				btnsSelectable.Add(_rotationButtons[0][AXIS_TO_INDEX[curCmdPart[0]]]);
				btnsSelectable.Add(_rotationButtons[1][AXIS_TO_INDEX[curCmdPart[1]]]);
			} else {
				switch (curCmdPart) {
					case "3d":
						if (!simulated4DMode) {
							yield return string.Format("sendtochaterror {0}, !{1} would already be in 3D mode after processing {2} command(s)", "{0}", "{1}", x);
							yield break;
						}
						btnsSelectable.Add(_3dModeButton);
						simulated4DMode = false;
						break;
					case "4d":
						if (simulated4DMode) {
							yield return string.Format("sendtochaterror {0}, !{1} would already be in 4D mode after processing {2} command(s)", "{0}", "{1}", x);
							yield break;
						}
						btnsSelectable.Add(_4dModeButton);
						simulated4DMode = true;
						break;
					case "next":
					case "r":
					case "right":
						btnsSelectable.Add(_nextButton);
						break;
					case "prev":
					case "l":
					case "left":
						btnsSelectable.Add(_prevButton);
						break;
					case "submit":
						btnsSelectable.Add(_submitButton);
						break;
					default:
						yield break;
				}
			}
		}
		for (int x = 0; x < btnsSelectable.Count; x++) {
			ButtonComponent btn = btnsSelectable[x];
			yield return null;
			btn.Selectable.OnInteract();
			while (_animation) yield return string.Format("trycancel Command process was canceled after {0} press(es).", x + 1);
		}
	}

	public IEnumerator TwitchHandleForcedSolve() {
		while (!IsSolved) {
			yield return new WaitUntil(() => !_animation);
			Set3DMode();
			while (_realSelectedHyperfaceIndex != _realSelectedPatternIndex) {
				NavButtonPressed(1);
				yield return new WaitForSeconds(.1f);
			}
			while (_hyperfaces[_realSelectedHyperfaceIndex].SelfRotation != _4D.Matrix5x5Int.IDENTITY) {
				int[] invalidAxles = Enumerable.Range(0, 3).Where(i => _hyperfaces[_realSelectedHyperfaceIndex].SelfRotation[i, i] != 1).ToArray();
				if (_selectedPrimaryRotationAxisIndex != invalidAxles[0]) {
					PrimaryRotationAxisPressed(invalidAxles[0]);
					yield return new WaitForSeconds(.1f);
				}
				SecondaryRotationAxisPressed(invalidAxles[1]);
				yield return new WaitUntil(() => !_animation);
			}
			SubmitButtonPressed();
			yield return new WaitForSeconds(.1f);
		}
	}

	private _4D.Matrix5x5 RotateUsingMouse(_4D.Matrix5x5 initial) {
		float x = Mathf.Clamp01(Input.mousePosition.x / Screen.width) * 2f - 1f;
		float y = Mathf.Clamp01(Input.mousePosition.y / Screen.height) * 2f - 1f;
		return RotationFromMousePos(initial, new Vector2(x, y));
	}

	private _4D.Matrix5x5 MouseEmulator(_4D.Matrix5x5 initial) {
		_mouseEmulatorTime += Time.deltaTime * MOUSE_EMULATOR_SPEED;
		if (_mouseEmulatorTime >= 9f) {
			_mouseEmulatorTime -= 9f;
			if (_mouseEmulatorTime >= 9f) _mouseEmulatorTime = 0f;
			float diff = 1f / 3f;
			_mouseEmulatorQueue = new[] { _mouseEmulatorQueue.Last() }.Concat(Enumerable.Range(0, 9).Select(i => {
				float x = Random.Range(-diff, diff) + (i % 3 - 1) * 2f * diff;
				float y = Random.Range(-diff, diff) + (i / 3 - 1) * 2f * diff;
				return new Vector2(x, y);
			}).OrderBy(_ => Random.Range(0f, 1f))).ToArray();
		}
		int fromInd = Mathf.FloorToInt(_mouseEmulatorTime);
		Vector2 from = _mouseEmulatorQueue[fromInd];
		Vector2 to = _mouseEmulatorQueue[fromInd + 1];
		float lerp = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * (_mouseEmulatorTime % 1));
		Vector2 mousePos = Vector2.Lerp(from, to, lerp);
		return RotationFromMousePos(initial, mousePos);
	}

	private _4D.Matrix5x5 RotationFromMousePos(_4D.Matrix5x5 initial, Vector2 mousePos) {
		return initial * _4D.Matrix5x5.Rotation(1, 0, mousePos.x * MOUSE_MAX_ROTATION_ANGLE) * _4D.Matrix5x5.Rotation(1, 2, mousePos.y * MOUSE_MAX_ROTATION_ANGLE);
	}

	private int PlaceRandomHyperface() {
		int faceIndex = Random.Range(0, 8);
		HyperfaceComponent face = _hyperfaces[faceIndex];
		face.Placed = true;
		face.Symbols = face.ExpectedSymbols;
		Log("Hyperface #{0} placed", faceIndex + 1);
		return faceIndex;
	}

	private void CreateHypercube() {
		Queue<Color> colorsQ = new Queue<Color>(new[] {
			new Color(1.0f, 0.0f, 0.0f, 1.0f),
			new Color(0.0f, 1.0f, 0.0f, 1.0f),
			new Color(0.0f, 0.0f, 1.0f, 1.0f),
			new Color(1.0f, 1.0f, 0.0f, 1.0f),
			new Color(1.0f, 0.0f, 1.0f, 1.0f),
			new Color(0.0f, 1.0f, 1.0f, 1.0f),
			new Color(1.0f, 1.0f, 1.0f, 1.0f),
			new Color(0.5f, 0.5f, 0.5f, 1.0f),
		}.OrderBy(_ => Random.Range(0f, 1f)));
		_hyperfaces = Enumerable.Range(0, 8).Select(_ => {
			HyperfaceComponent hyperface = Instantiate(HyperfacePrefab);
			hyperface.transform.parent = HypercubeContainer;
			hyperface.transform.localPosition = Vector3.zero;
			hyperface.transform.localScale = Vector3.one;
			hyperface.transform.localRotation = Quaternion.identity;
			hyperface.Renderer.material.color = colorsQ.Dequeue();
			return hyperface;
		}).ToArray();
		_hyperfaces[0].LocalTransform = _4D.Matrix5x5Int.Translation(_4D.Vector4Int.ANA);
		_hyperfaces[1].LocalTransform = _4D.Matrix5x5Int.ROTATION_XW * _4D.Matrix5x5Int.Translation(_4D.Vector4Int.LEFT);
		_hyperfaces[2].LocalTransform = _4D.Matrix5x5Int.ROTATION_WX * _4D.Matrix5x5Int.Translation(_4D.Vector4Int.RIGHT);
		_hyperfaces[3].LocalTransform = _4D.Matrix5x5Int.ROTATION_YW * _4D.Matrix5x5Int.Translation(_4D.Vector4Int.DOWN);
		_hyperfaces[4].LocalTransform = _4D.Matrix5x5Int.ROTATION_WY * _4D.Matrix5x5Int.Translation(_4D.Vector4Int.UP);
		_hyperfaces[5].LocalTransform = _4D.Matrix5x5Int.ROTATION_ZW * _4D.Matrix5x5Int.Translation(_4D.Vector4Int.BACK);
		_hyperfaces[6].LocalTransform = _4D.Matrix5x5Int.ROTATION_WZ * _4D.Matrix5x5Int.Translation(_4D.Vector4Int.FRONT);
		_hyperfaces[7].LocalTransform = _4D.Matrix5x5Int.ROTATION_XW * _4D.Matrix5x5Int.ROTATION_XW * _4D.Matrix5x5Int.Translation(_4D.Vector4Int.KATA);
	}

	private void SetExpectedSymbols() {
		int cubeIndex = Random.Range(0, PatternHypercubeService.HYPERCUBES_COUNT);
		Log("Hypercube #{0}/10", cubeIndex + 1);
		int[][] symbols = PatternHypercubeService.GetHypercube(RuleSeedId, cubeIndex);
		// foreach (int[] face in symbols) Debug.Log(face.Join(", "));
		for (int i = 0; i < 8; i++) _hyperfaces[i].ExpectedSymbols = Enumerable.Range(0, 6).Select(j => symbols[i][j]).ToArray();
	}

	private void CreateButtons() {
		List<KMSelectable> newSelectables = new List<KMSelectable>();
		float buttonsOffset = 0.02f;
		_rotationButtons = Enumerable.Range(0, 2).Select(_ => Enumerable.Repeat<ButtonComponent>(null, 4).ToArray()).ToArray();
		for (int axisIndex = 0; axisIndex < 4; axisIndex++) {
			int axisIndexClosure = axisIndex;
			for (int i = 0; i < 2; i++) {
				Vector3 pos = i * Vector3.right * buttonsOffset + axisIndex * Vector3.back * buttonsOffset;
				ButtonComponent button = CreateOneButton(RightButtonsContainer, pos, AXLES_NAMES[axisIndex], Color.gray, newSelectables);
				_rotationButtons[i][axisIndex] = button;
				if (i == 0) button.Selectable.OnInteract += () => { PrimaryRotationAxisPressed(axisIndexClosure); return false; };
				else button.Selectable.OnInteract += () => { SecondaryRotationAxisPressed(axisIndexClosure); return false; };
			}
		}
		_submitButton = CreateOneButton(RightButtonsContainer, Vector3.forward * 0.02f + Vector3.right * buttonsOffset / 2f, "SUBMIT", Color.white, newSelectables);
		_submitButton.MakeWide();
		_submitButton.Selectable.OnInteract += () => { SubmitButtonPressed(); return false; };
		float modeButtonsOffset = 0.01f;
		_3dModeButton = CreateOneButton(TopButtonsContainer, (Vector3.left + Vector3.back) * modeButtonsOffset, "3D", Color.gray, newSelectables);
		_3dModeButton.Selectable.OnInteract += () => { Set3DMode(); return false; };
		_4dModeButton = CreateOneButton(TopButtonsContainer, (Vector3.right + Vector3.back) * modeButtonsOffset, "4D", Color.white, newSelectables);
		_4dModeButton.Selectable.OnInteract += () => { Set4DMode(); return false; };
		_nextButton = CreateOneButton(TopButtonsContainer, (Vector3.right + Vector3.forward) * modeButtonsOffset, ">", Color.white, newSelectables);
		_nextButton.Selectable.OnInteract += () => { NavButtonPressed(1); return false; };
		_prevButton = CreateOneButton(TopButtonsContainer, (Vector3.left + Vector3.forward) * modeButtonsOffset, "<", Color.white, newSelectables);
		_prevButton.Selectable.OnInteract += () => { NavButtonPressed(-1); return false; };
		Selectable.Children = newSelectables.ToArray();
		Selectable.UpdateChildren();
	}

	private ButtonComponent CreateOneButton(Transform parent, Vector3 pos, string label, Color color, List<KMSelectable> selectables) {
		ButtonComponent result = Instantiate(ButtonPrefab);
		result.transform.parent = parent;
		result.transform.localPosition = pos;
		result.transform.localScale = Vector3.one;
		result.transform.localRotation = Quaternion.identity;
		result.Selectable.Parent = Selectable;
		result.Label = label;
		result.LabelColor = color;
		selectables.Add(result.Selectable);
		return result;
	}

	private void PrimaryRotationAxisPressed(int axisIndex) {
		if (_animation) return;
		if (axisIndex == _selectedPrimaryRotationAxisIndex) return;
		if (!_4dMode && axisIndex == 3) return;
		if (_selectedPrimaryRotationAxisIndex >= 0) _rotationButtons[0][_selectedPrimaryRotationAxisIndex].LabelColor = Color.gray;
		_rotationButtons[0][axisIndex].LabelColor = Color.white;
		_selectedPrimaryRotationAxisIndex = axisIndex;
		Audio.PlaySoundAtTransform(ButtonPressSounds[Random.Range(0, ButtonPressSounds.Length)], transform);
	}

	private void SecondaryRotationAxisPressed(int axisIndex) {
		if (_animation) return;
		if (_selectedPrimaryRotationAxisIndex < 0) return;
		if (_selectedPrimaryRotationAxisIndex == axisIndex) return;
		if (!_4dMode && (_solved || axisIndex == 3)) return;
		_animation = true;
		_animationEndsAt = Time.time + ANIMATION_DURATION;
		_rotationButtons[1][axisIndex].LabelColor = Color.white;
		_animationPrimaryAxisIndex = _selectedPrimaryRotationAxisIndex;
		_animationSecondaryAxisIndex = axisIndex;
		_4D.Matrix5x5Int fromRotation = _4dMode ? _hypercubeRotation : _hyperfaces[_realSelectedHyperfaceIndex].SelfRotation;
		_hypercubeRotationAfterAnimation = fromRotation * _4D.Matrix5x5Int.Rotation(_selectedPrimaryRotationAxisIndex, axisIndex);
		Audio.PlaySoundAtTransform(_4dMode ? "HypercubeRotation" : "HyperfaceRotation", transform);
	}

	private void NavButtonPressed(int diff) {
		if (_animation || _solved) return;
		int prevHF = _realSelectedHyperfaceIndex;
		int prevPF = _realSelectedPatternIndex;
		if (_4dMode) {
			_hyperfaces[_realSelectedHyperfaceIndex].ResetSymbols();
			do { _selectedHyperfaceIndex = (_selectedHyperfaceIndex + 8 + diff) % 8; } while (_hyperfaces[_realSelectedHyperfaceIndex].Placed);
			if (prevHF != _realSelectedHyperfaceIndex) Audio.PlaySoundAtTransform(ButtonPressSounds[Random.Range(0, ButtonPressSounds.Length)], transform);
		} else {
			do { _selectedPatternIndex = (_selectedPatternIndex + 8 + diff) % 8; } while (_hyperfaces[_realSelectedPatternIndex].Placed);
			if (prevPF != _realSelectedPatternIndex) Audio.PlaySoundAtTransform(ButtonPressSounds[Random.Range(0, ButtonPressSounds.Length)], transform);
		}
		_hyperfaces[_realSelectedHyperfaceIndex].Symbols = _hyperfaces[_realSelectedPatternIndex].ExpectedSymbols;
	}

	private void SubmitButtonPressed() {
		if (_animation || _solved) return;
		if (_realSelectedHyperfaceIndex != _realSelectedPatternIndex) {
			Log("Trying to place pattern of hyperface #{0} on hyperface #{1}", _realSelectedPatternIndex + 1, _realSelectedHyperfaceIndex + 1);
			Strike();
		} else if (_hyperfaces[_realSelectedHyperfaceIndex].SelfRotation != _4D.Matrix5x5Int.IDENTITY) {
			Log("Trying to place valid pattern on hyperface #{0} but in invalid orientation", _realSelectedHyperfaceIndex + 1);
			for (int row = 0; row < 5; row++) {
				Debug.Log(Enumerable.Range(0, 5).Select(col => _hyperfaces[_realSelectedHyperfaceIndex].SelfRotation[row, col]).Join(", "));
			}
			Strike();
		} else {
			Log("Pattern placed on hyperface #{0}", _realSelectedHyperfaceIndex + 1);
			_hyperfaces[_realSelectedHyperfaceIndex].Placed = true;
			if (_hyperfaces.All(f => f.Placed)) {
				Log("Hypercube filled. Module solved!");
				Solve();
				Audio.PlayGameSoundAtTransform(KMSoundOverride.SoundEffect.CorrectChime, transform);
				_solved = true;
			} else {
				do { _selectedHyperfaceIndex = (_selectedHyperfaceIndex + 1) % 8; } while (_hyperfaces[_realSelectedHyperfaceIndex].Placed);
				do { _selectedPatternIndex = (_selectedPatternIndex + 1) % 8; } while (_hyperfaces[_realSelectedPatternIndex].Placed);
				_hyperfaces[_realSelectedHyperfaceIndex].Symbols = _hyperfaces[_realSelectedPatternIndex].ExpectedSymbols;
				Audio.PlaySoundAtTransform("HyperfacePlaced", transform);
			}
		}
	}

	private void Set3DMode() {
		if (_animation || !_4dMode) return;
		_4dMode = false;
		_4dModeButton.LabelColor = Color.gray;
		_3dModeButton.LabelColor = Color.white;
		if (_selectedPrimaryRotationAxisIndex == 3) _selectedPrimaryRotationAxisIndex = -1;
		_rotationButtons[0][3].LabelColor = Color.black;
		_rotationButtons[1][3].LabelColor = Color.black;
		Audio.PlaySoundAtTransform(ButtonPressSounds[Random.Range(0, ButtonPressSounds.Length)], transform);
	}

	private void Set4DMode() {
		if (_animation || _4dMode) return;
		_4dMode = true;
		_4dModeButton.LabelColor = Color.white;
		_3dModeButton.LabelColor = Color.gray;
		_rotationButtons[0][3].LabelColor = Color.gray;
		_rotationButtons[1][3].LabelColor = Color.gray;
		Audio.PlaySoundAtTransform(ButtonPressSounds[Random.Range(0, ButtonPressSounds.Length)], transform);
	}
}
